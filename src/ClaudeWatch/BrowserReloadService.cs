using System.Text.Json;
using System.Threading.Channels;

namespace ClaudeWatch;

/// <summary>An SSE event sent to connected browser tabs.</summary>
public sealed record SseEvent(string Name, string JsonData)
{
    public static SseEvent Reload() => new("reload", "{}");

    public static SseEvent CssUpdate(string route, string url) =>
        new("update-css", JsonSerializer.Serialize(new { path = route, url }));
}

public interface IReloadBroadcaster
{
    int Broadcast();
    int BroadcastCssUpdate(string route, string url);
}

/// <summary>Registry of connected SSE clients; broadcasts reload / css-update events to every tab.</summary>
public sealed class BrowserReloadService : IReloadBroadcaster
{
    private readonly List<Channel<SseEvent>> _clients = [];
    private readonly Lock _lock = new();

    public int ClientCount { get { lock (_lock) return _clients.Count; } }

    public int Broadcast() => Send(SseEvent.Reload());

    public int BroadcastCssUpdate(string route, string url) => Send(SseEvent.CssUpdate(route, url));

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
