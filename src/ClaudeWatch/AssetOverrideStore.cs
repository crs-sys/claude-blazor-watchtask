namespace ClaudeWatch;

/// <summary>
/// Routes the watcher serves fresh copies of after a css-only round. Under MapStaticAssets the
/// app can only serve build-time-fingerprinted content, so post-build CSS is served from here
/// (GET /asset/{route}) until the next full round makes file and manifest consistent again.
/// Only registered routes are ever served — this is a dictionary lookup, not a file server.
/// </summary>
public sealed class AssetOverrideStore
{
    private readonly Dictionary<string, string> _routeToFile = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    public void Register(string route, string absoluteFilePath)
    {
        lock (_lock) _routeToFile[Normalize(route)] = absoluteFilePath;
    }

    public bool TryGet(string route, out string absoluteFilePath)
    {
        lock (_lock) return _routeToFile.TryGetValue(Normalize(route), out absoluteFilePath!);
    }

    public IReadOnlyList<string> Routes
    {
        get { lock (_lock) return _routeToFile.Keys.ToList(); }
    }

    public void Clear()
    {
        lock (_lock) _routeToFile.Clear();
    }

    private static string Normalize(string route) => route.Replace('\\', '/').TrimStart('/');
}
