using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ClaudeWatch;

public interface IAppSupervisor
{
    bool IsRunning { get; }
    int? Pid { get; }
    DateTimeOffset? StartedAt { get; }
    Task StopAsync();
    /// <summary>Starts the app and waits for readiness. Returns null on success, else a failure description.</summary>
    Task<string?> StartAsync(CancellationToken ct);
}

public sealed class AppSupervisor(WatchConfig config, bool echoAppOutput = true) : IAppSupervisor, IDisposable
{
    private Process? _process;
    private JobObject? _job;
    private readonly OutputTail _tail = new(30);

    public bool IsRunning => _process is { HasExited: false };
    public int? Pid => IsRunning ? _process!.Id : null;
    public DateTimeOffset? StartedAt { get; private set; }
    public IReadOnlyList<string> OutputTail => _tail.Lines;

    public async Task StopAsync()
    {
        if (_process is null) return;
        var process = _process;
        _process = null;
        StartedAt = null;

        // Job Object terminate kills the whole tree (dotnet run wrapper + real app exe)
        _job?.Terminate();
        _job?.Dispose();
        _job = null;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true); // non-Windows / belt-and-suspenders
                await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
            }
        }
        catch { }
        finally { process.Dispose(); }
    }

    public async Task<string?> StartAsync(CancellationToken ct)
    {
        await StopAsync();

        var psi = ProcessUtil.Create(config.Run, config); // env/inheritEnv applied there
        psi.Environment["CLAUDE_WATCH_PORT"] = config.Server.Port.ToString();

        var readyRegex = new Regex(config.Run.Readiness.StdoutRegex, RegexOptions.IgnoreCase);
        var readySignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        void OnLine(string? line)
        {
            if (line is null) return;
            _tail.Add(line);
            if (echoAppOutput) Log.App(line);
            if (readyRegex.IsMatch(line)) readySignal.TrySetResult();
        }
        process.OutputDataReceived += (_, e) => OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => OnLine(e.Data);

        _job = new JobObject();
        process.Start();
        try { _job.Assign(process); }
        catch { /* process may have exited instantly; the wait below reports it */ }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;
        StartedAt = DateTimeOffset.Now;

        var failure = await ReadinessProbe.WaitAsync(process, readySignal.Task, config.Run.Readiness, ct);
        if (failure is not null)
        {
            await StopAsync();
            return failure;
        }
        return null;
    }

    public void Dispose()
    {
        _job?.Terminate();
        _job?.Dispose();
        _process?.Dispose();
    }
}

public static class ReadinessProbe
{
    /// <summary>Returns null when ready; otherwise a failure description.</summary>
    public static async Task<string?> WaitAsync(
        Process process, Task stdoutReady, WatchConfig.ReadinessConfig readiness, CancellationToken ct)
    {
        using var http = readiness.ProbeUrl is null ? null : new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true // dev certs
        }) { Timeout = TimeSpan.FromSeconds(2) };

        var deadline = DateTimeOffset.Now.AddSeconds(readiness.TimeoutSec);
        while (DateTimeOffset.Now < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited)
                return $"app exited during startup (exit code {process.ExitCode})";
            if (stdoutReady.IsCompleted)
                return null;
            if (http is not null)
            {
                try
                {
                    using var response = await http.GetAsync(readiness.ProbeUrl, ct);
                    return null; // any HTTP response means the server is up
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { /* not up yet */ }
            }
            await Task.Delay(500, ct);
        }
        return $"app did not become ready within {readiness.TimeoutSec}s";
    }
}
