namespace ClaudeWatch;

/// <summary>
/// FileSystemWatcher over configured paths, feeding the same change journal. Two modes:
/// "journal" — only journals changes (the Claude Stop hook still decides when to rebuild);
/// catches edits made by scripts that bypass the Edit/Write tool hooks.
/// "trigger" — hook-free operation: a quiet-period debounce after changes triggers the round.
/// </summary>
public sealed class FallbackWatcher(WatchConfig config, ChangeJournal journal, Pipeline pipeline) : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private Timer? _debounce;
    private readonly Lock _lock = new();

    public void Start()
    {
        foreach (var relative in config.FallbackWatch.Paths)
        {
            var path = config.ResolvePath(relative);
            if (!Directory.Exists(path))
            {
                Log.Warn($"fallback watch path does not exist: {path}");
                continue;
            }
            var watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
            };
            watcher.Changed += (_, e) => OnChange(e.FullPath);
            watcher.Created += (_, e) => OnChange(e.FullPath);
            watcher.Deleted += (_, e) => OnChange(e.FullPath);
            watcher.Renamed += (_, e) => OnChange(e.FullPath);
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
        Log.Info(IsJournalMode
            ? $"file journal active on {_watchers.Count} path(s) — scripted edits are detected; rounds still trigger on Claude Stop"
            : $"fallback file watch active on {_watchers.Count} path(s), quiet period {config.FallbackWatch.QuietPeriodSec}s");
    }

    private bool IsJournalMode =>
        config.FallbackWatch.Mode.Equals("journal", StringComparison.OrdinalIgnoreCase);

    private void OnChange(string fullPath)
    {
        if (Directory.Exists(fullPath)) return; // directory events (deleted files pass through)
        var normalized = Globs.Normalize(fullPath, config.RepoRoot);
        if (Globs.MatchesAny(normalized, config.Classify.Exclude)) return; // critical: App_Data etc. must not self-trigger

        journal.Add(fullPath);
        if (IsJournalMode) return; // Stop hook decides when to rebuild

        lock (_lock)
        {
            // restart the quiet-period countdown on every relevant change
            _debounce?.Dispose();
            _debounce = new Timer(_ => pipeline.Post(new Trigger(TriggerKind.FallbackWatch)),
                null, TimeSpan.FromSeconds(config.FallbackWatch.QuietPeriodSec), Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        lock (_lock) _debounce?.Dispose();
    }
}
