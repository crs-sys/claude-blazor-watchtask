using System.Diagnostics;

namespace ClaudeWatch;

public interface IStepRunner
{
    Task<bool> RunAsync(WatchConfig.PreBuildStep step, CancellationToken ct);
}

public sealed class StepRunner(WatchConfig config) : IStepRunner
{
    public async Task<bool> RunAsync(WatchConfig.PreBuildStep step, CancellationToken ct)
    {
        var psi = ProcessUtil.Create(step, config);
        using var process = new Process { StartInfo = psi };
        var tail = new OutputTail(20);
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) tail.Add(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) tail.Add(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(step.TimeoutSec));
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            Log.Error($"step '{step.Name}' timed out after {step.TimeoutSec}s");
            return false;
        }

        if (process.ExitCode != 0)
        {
            Log.Error($"step '{step.Name}' failed (exit {process.ExitCode}):");
            foreach (var line in tail.Lines) Log.App(line);
            return false;
        }
        return true;
    }
}

public static class ProcessUtil
{
    public static ProcessStartInfo Create(WatchConfig.CommandConfig cmd, WatchConfig config)
    {
        var workingDir = config.ResolvePath(cmd.WorkingDir);
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (!cmd.InheritEnv) psi.Environment.Clear();
        foreach (var (key, value) in cmd.Env) psi.Environment[key] = value;

        var resolved = ResolveCommand(cmd.Command);
        // A relative path with separators (e.g. "bin/Debug/net10.0/Sra.exe") is meant relative
        // to the command's workingDir, not the watcher's cwd
        if (!Path.IsPathRooted(resolved) && (resolved.Contains('/') || resolved.Contains('\\')))
            resolved = Path.GetFullPath(resolved, workingDir);
        var ext = Path.GetExtension(resolved);
        if (OperatingSystem.IsWindows() &&
            (ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
             ext.Equals(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            // .cmd/.bat (npm, npx, ...) can't be spawned directly with UseShellExecute=false
            psi.FileName = "cmd.exe";
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(resolved);
        }
        else
        {
            psi.FileName = resolved;
        }
        foreach (var arg in cmd.Args) psi.ArgumentList.Add(arg);
        return psi;
    }

    /// <summary>
    /// On Windows, commands like "npm" resolve to npm.cmd, which UseShellExecute=false can't
    /// launch directly — probe PATH/PATHEXT for the real file.
    /// </summary>
    public static string ResolveCommand(string command)
    {
        if (!OperatingSystem.IsWindows()) return command;
        if (Path.HasExtension(command) || command.Contains('\\') || command.Contains('/')) return command;

        var pathExt = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in paths)
        {
            foreach (var ext in pathExt)
            {
                var candidate = Path.Combine(dir.Trim(), command + ext.ToLowerInvariant());
                if (File.Exists(candidate)) return candidate;
            }
        }
        return command;
    }
}

public sealed class OutputTail(int capacity)
{
    private readonly Queue<string> _lines = new();
    private readonly Lock _lock = new();

    public void Add(string line)
    {
        lock (_lock)
        {
            _lines.Enqueue(line);
            while (_lines.Count > capacity) _lines.Dequeue();
        }
    }

    public IReadOnlyList<string> Lines
    {
        get { lock (_lock) return _lines.ToList(); }
    }
}
