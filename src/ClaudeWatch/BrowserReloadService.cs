using System.Text.Json;
using System.Threading.Channels;

namespace ClaudeWatch;

/// <summary>An SSE event sent to connected browser tabs.</summary>
public sealed record SseEvent(string Name, string JsonData)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static SseEvent Reload() => new("reload", "{}");

    public static SseEvent CssUpdate(string route, string url, bool replay = false) =>
        new("update-css", JsonSerializer.Serialize(new { path = route, url, replay }));

    /// <summary>A round started — tabs animate the title as a "rebuilding" indicator.</summary>
    public static SseEvent Building(int round) =>
        new("building", JsonSerializer.Serialize(new { round }));

    /// <summary>The round failed — tabs render the errors as a full-screen overlay.</summary>
    public static SseEvent BuildError(int round, IEnumerable<BuildError> errors) =>
        new("build-error", JsonSerializer.Serialize(new
        {
            round,
            errors = errors.Select(e => new { file = e.File, line = e.Line, code = e.Code, message = e.Message }),
        }, Json));
}

public interface IReloadBroadcaster
{
    int Broadcast();
    int BroadcastCssUpdate(string route, string url);
    int BroadcastBuilding(int round);
    int BroadcastBuildError(int round, IEnumerable<BuildError> errors);
}

/// <summary>Registry of connected SSE clients; broadcasts reload / css-update events to every tab.</summary>
public sealed class BrowserReloadService : IReloadBroadcaster
{
    private readonly List<Channel<SseEvent>> _clients = [];
    private readonly Lock _lock = new();

    public int ClientCount { get { lock (_lock) return _clients.Count; } }

    public int Broadcast() => Send(SseEvent.Reload());

    public int BroadcastCssUpdate(string route, string url) => Send(SseEvent.CssUpdate(route, url));

    public int BroadcastBuilding(int round) => Send(SseEvent.Building(round));

    public int BroadcastBuildError(int round, IEnumerable<BuildError> errors) => Send(SseEvent.BuildError(round, errors));

    private int Send(SseEvent evt)
    {
        lock (_lock)
        {
            foreach (var client in _clients) client.Writer.TryWrite(evt);
            return _clients.Count;
        }
    }

    public Channel<SseEvent> Register()
    {
        var channel = Channel.CreateUnbounded<SseEvent>();
        lock (_lock) _clients.Add(channel);
        return channel;
    }

    public void Unregister(Channel<SseEvent> channel)
    {
        lock (_lock) _clients.Remove(channel);
    }
}
