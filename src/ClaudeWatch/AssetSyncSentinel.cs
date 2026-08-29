namespace ClaudeWatch;

/// <summary>
/// Detects the MapStaticAssets desync trap: static assets are served with build-time
/// fingerprints (Content-Length, ETag, precompressed .gz), so any process that rewrites a
/// pre-build step's output file AFTER `dotnet build` (classic culprit: a `tailwind --watch`
/// left running in another terminal) makes browsers receive broken or stale CSS while direct
/// fetches of the file still look fine. The pipeline snapshots step outputs after each
/// successful build; a timer compares while the app runs and warns loudly on drift.
/// </summary>
public sealed class AssetSyncSentinel(WatchConfig config) : IDisposable
{
    private sealed record Snapshot(long Length, DateTime MtimeUtc);

    private readonly Dictionary<string, Snapshot> _snapshots = [];
    private readonly HashSet<string> _warned = [];
    private readonly Lock _lock = new();
    private Timer? _timer;

    /// <summary>Files that changed after the build and are being served with stale fingerprints.</summary>
    public IReadOnlyList<string> StaleFiles
    {
        get { lock (_lock) return _warned.ToList(); }
    }

    /// <summary>Call right after a successful build: record what the build fingerprinted.</summary>
    public void CaptureAfterBuild()
    {
        lock (_lock)
        {
            _snapshots.Clear();
            _warned.Clear();
            foreach (var step in config.PreBuildSteps)
            {
                if (step.Output is not { Length: > 0 } output) continue;
                var path = config.ResolvePath(output);
                var info = new FileInfo(path);
                if (info.Exists)
                    _snapshots[path] = new Snapshot(info.Length, info.LastWriteTimeUtc);
            }
        }
        _timer ??= new Timer(_ => Check(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public void Check()
    {
        List<string> newlyStale = [];
        lock (_lock)
        {
            foreach (var (path, snapshot) in _snapshots)
            {
                if (_warned.Contains(path)) continue;
                var info = new FileInfo(path);
                var changed = !info.Exists
                              || info.Length != snapshot.Length
                              || info.LastWriteTimeUtc != snapshot.MtimeUtc;
                if (changed && _warned.Add(path)) newlyStale.Add(path);
            }
        }
        foreach (var path in newlyStale)
        {
            Log.Warn($"STALE ASSET: {Path.GetFileName(path)} was rewritten AFTER the build.");
            Log.Warn("  MapStaticAssets serves build-time fingerprints, so browsers now get broken or stale CSS");
            Log.Warn("  (direct fetches of the file will still look correct). Likely culprit: a `tailwind --watch`");
            Log.Warn("  (npm run ui:dev) left running in another terminal — stop it; claude-watch runs the");
            Log.Warn("  tailwind build itself each round. Press R to rebuild and resync.");
        }
    }

    public void Dispose() => _timer?.Dispose();
}
