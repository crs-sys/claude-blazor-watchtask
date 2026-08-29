using System.Threading.Channels;

namespace ClaudeWatch;

public interface IReloadBroadcaster
{
    int Broadcast();
}

/// <summary>Registry of connected SSE clients; Broadcast() tells every browser tab to reload.</summary>
public sealed class BrowserReloadService : IReloadBroadcaster
{
    private readonly List<Channel<string>> _clients = [];
    private readonly Lock _lock = new();

    public int ClientCount { get { lock (_lock) return _clients.Count; } }

    public int Broadcast()
    {
        lock (_lock)
        {
            foreach (var client in _clients) client.Writer.TryWrite("reload");
            return _clients.Count;
        }
    }

    public Channel<string> Register()
    {
        var channel = Channel.CreateUnbounded<string>();
        lock (_lock) _clients.Add(channel);
        return channel;
    }

    public void Unregister(Channel<string> channel)
    {
        lock (_lock) _clients.Remove(channel);
    }
}
