using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClaudeWatch;

/// <summary>
/// Localhost HTTP endpoint the Claude Code hooks talk to. All Claude payload parsing lives
/// here (server-side) so the hook scripts stay dumb one-liners.
/// </summary>
public sealed class TriggerServer(
    WatchConfig config,
    ChangeJournal journal,
    Pipeline pipeline,
    IAppSupervisor supervisor,
    BrowserReloadService reloadService,
    AssetSyncSentinel? sentinel = null)
{
    private WebApplication? _app;

    public async Task StartAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.ListenLocalhost(config.Server.Port));

        var app = builder.Build();

        // Hooks pipe Claude's stdin JSON straight through; extract file paths server-side.
        app.MapPost("/hook/post-tool-use", async context =>
        {
            foreach (var path in await ExtractFilePathsAsync(context.Request.Body))
                journal.Add(path);
            context.Response.StatusCode = 200;
        });

        app.MapPost("/hook/stop", async context =>
        {
            var sessionId = await ExtractSessionIdAsync(context.Request.Body);
            pipeline.Post(new Trigger(TriggerKind.ClaudeStop, sessionId));
            context.Response.StatusCode = 200;
        });

        app.MapPost("/changed", async context =>
        {
            foreach (var path in await ExtractFilePathsAsync(context.Request.Body))
                journal.Add(path);
            context.Response.StatusCode = 200;
        });

        app.MapPost("/trigger", () => { pipeline.Post(new Trigger(TriggerKind.Api)); return Results.Ok(); });
        app.MapPost("/force", () => { pipeline.Post(new Trigger(TriggerKind.Manual)); return Results.Ok(); });

        app.MapGet("/status", () => Results.Json(BuildStatus(), StatusJsonOptions));

        app.MapGet("/events", HandleSseAsync);

        app.MapGet("/claude-watch-reload.js", context =>
        {
            context.Response.ContentType = "application/javascript";
            context.Response.Headers.AccessControlAllowOrigin = "*";
            return context.Response.WriteAsync(ReadEmbedded("reload.js"), context.RequestAborted);
        });

        await app.StartAsync(ct);
        _app = app;
    }

    public async Task StopAsync()
    {
        if (_app is not null) await _app.StopAsync();
    }

    private async Task HandleSseAsync(HttpContext context)
    {
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.AccessControlAllowOrigin = "*";
        await context.Response.WriteAsync(": connected\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);

        var channel = reloadService.Register();
        try
        {
            await foreach (var evt in channel.Reader.ReadAllAsync(context.RequestAborted))
            {
                await context.Response.WriteAsync($"event: {evt}\ndata: {{}}\n\n", context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            reloadService.Unregister(channel);
        }
    }

    private static readonly JsonSerializerOptions StatusJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public object BuildStatus() => new
    {
        State = pipeline.State.ToString(),
        Round = pipeline.Round,
        App = new
        {
            Running = supervisor.IsRunning,
            Pid = supervisor.Pid,
            Url = config.Run.AppUrl,
            StartedAt = supervisor.StartedAt,
        },
        LastBuild = pipeline.LastBuild is { } b
            ? new
            {
                b.Success,
                DurationMs = (long)b.Duration.TotalMilliseconds,
                b.FinishedAt,
                Errors = b.Errors.Select(e => new { e.File, e.Line, e.Code, e.Message }),
            }
            : null,
        PendingChanges = journal.Peek(),
        LastRoundFiles = pipeline.LastRoundFileCount,
        LastRoundSkipped = pipeline.LastRoundSkipped,
        ReloadClients = reloadService.ClientCount,
        // Non-empty = a step output (e.g. tailwind's app.css) was rewritten after the build;
        // browsers are getting broken/stale CSS until the next round
        StaleAssets = sentinel?.StaleFiles ?? [],
    };

    /// <summary>
    /// Accepts either a raw Claude PostToolUse payload ({tool_name, tool_input:{file_path,...}})
    /// or the simple form {"file_path": "..."}.
    /// </summary>
    public static async Task<List<string>> ExtractFilePathsAsync(Stream body)
    {
        var paths = new List<string>();
        try
        {
            using var doc = await JsonDocument.ParseAsync(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return paths;

            CollectPaths(root, paths);
            if (root.TryGetProperty("tool_input", out var toolInput) && toolInput.ValueKind == JsonValueKind.Object)
            {
                CollectPaths(toolInput, paths);
                if (toolInput.TryGetProperty("edits", out var edits) && edits.ValueKind == JsonValueKind.Array)
                    foreach (var edit in edits.EnumerateArray())
                        if (edit.ValueKind == JsonValueKind.Object)
                            CollectPaths(edit, paths);
            }
        }
        catch (JsonException) { }
        return paths;

        static void CollectPaths(JsonElement obj, List<string> paths)
        {
            foreach (var name in new[] { "file_path", "notebook_path" })
                if (obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String &&
                    p.GetString() is { Length: > 0 } value)
                    paths.Add(value);
        }
    }

    public static async Task<string?> ExtractSessionIdAsync(Stream body)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("session_id", out var sid) &&
                sid.ValueKind == JsonValueKind.String)
                return sid.GetString();
        }
        catch (JsonException) { }
        return null;
    }

    public static string ReadEmbedded(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Embedded resource not found: {name}");
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
