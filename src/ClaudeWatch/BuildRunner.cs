using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ClaudeWatch;

public sealed record BuildError(string File, int Line, string Code, string Message);

public sealed record BuildResult(
    bool Success,
    TimeSpan Duration,
    IReadOnlyList<BuildError> Errors,
    IReadOnlyList<string> OutputTail,
    DateTimeOffset FinishedAt);

public interface IBuildRunner
{
    Task<BuildResult> BuildAsync(CancellationToken ct);
}

public sealed partial class BuildRunner(WatchConfig config) : IBuildRunner
{
    // Canonical MSBuild diagnostic: path\file.cs(12,34): error CS0103: message [proj.csproj]
    [GeneratedRegex(@"^\s*(?<file>[^(]+)\((?<line>\d+)(,\d+)?\)\s*:\s*error\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<msg>.*?)(\s*\[[^\]]+\])?\s*$")]
    private static partial Regex ErrorLine();

    // Errors without a file location: "MSBUILD : error MSB1009: ..." or "error NETSDK1004: ..."
    [GeneratedRegex(@"^\s*(?<file>[^:(]*?)\s*:\s*error\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<msg>.*?)(\s*\[[^\]]+\])?\s*$")]
    private static partial Regex BareErrorLine();

    public async Task<BuildResult> BuildAsync(CancellationToken ct)
    {
        var psi = ProcessUtil.Create(config.Build, config);
        using var process = new Process { StartInfo = psi };
        var lines = new List<string>();
        var lineLock = new Lock();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (lineLock) lines.Add(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (lineLock) lines.Add(e.Data); };

        var stopwatch = Stopwatch.StartNew();
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(ct);
        stopwatch.Stop();

        List<string> output;
        lock (lineLock) output = lines.ToList();

        var errors = ParseErrors(output);
        var tail = output.Skip(Math.Max(0, output.Count - 20)).ToList();
        return new BuildResult(process.ExitCode == 0, stopwatch.Elapsed, errors, tail, DateTimeOffset.Now);
    }

    public static IReadOnlyList<BuildError> ParseErrors(IEnumerable<string> outputLines)
    {
        var errors = new List<BuildError>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in outputLines)
        {
            BuildError? error = null;
            var m = ErrorLine().Match(line);
            if (m.Success)
            {
                error = new BuildError(m.Groups["file"].Value.Trim(), int.Parse(m.Groups["line"].Value),
                    m.Groups["code"].Value, m.Groups["msg"].Value.Trim());
            }
            else
            {
                var b = BareErrorLine().Match(line);
                if (b.Success)
                    error = new BuildError(b.Groups["file"].Value.Trim(), 0,
                        b.Groups["code"].Value, b.Groups["msg"].Value.Trim());
            }
            // MSBuild repeats diagnostics once per referencing project — dedup
            if (error is not null && seen.Add($"{error.File}|{error.Line}|{error.Code}|{error.Message}"))
                errors.Add(error);
        }
        return errors;
    }
}
