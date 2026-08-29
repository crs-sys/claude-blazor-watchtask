using System.Text.Json;

namespace ClaudeWatch;

/// <summary>
/// `claude-watch status [--port N]` — one-shot query of a running watcher.
/// Exit code 0 when the last build succeeded (or none yet), 1 on build failure/app down,
/// 2 when no watcher is reachable — so Claude (or a skill) can script against it.
/// </summary>
public static class StatusCommand
{
    public static async Task<int> RunAsync(int port)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        string json;
        try
        {
            json = await http.GetStringAsync($"http://127.0.0.1:{port}/status");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Console.Error.WriteLine($"No claude-watch instance reachable on port {port}.");
            return 2;
        }

        Console.WriteLine(json);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var buildOk = !root.TryGetProperty("lastBuild", out var lastBuild)
                      || lastBuild.ValueKind == JsonValueKind.Null
                      || (lastBuild.TryGetProperty("success", out var s) && s.GetBoolean());
        var appRunning = root.TryGetProperty("app", out var app) &&
                         app.TryGetProperty("running", out var r) && r.GetBoolean();
        return buildOk && appRunning ? 0 : 1;
    }
}
