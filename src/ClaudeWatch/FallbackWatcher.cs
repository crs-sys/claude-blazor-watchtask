namespace ClaudeWatch;

/// <summary>
/// FileSystemWatcher over configured paths, feeding the same change journal. Modes:
/// "journal" — only journals changes (the Claude Stop hook still decides when to rebuild);
/// catches edits made by scripts that bypass the Edit/Write tool hooks.
/// "hybrid" — journal + self-trigger for editor edits, but the trigger is HELD while a Claude
/// turn is in flight (the Stop-hook round picks the edits up instead); re-checked every quiet
/// period until the agent goes idle.
/// "trigger" — hook-free operation: a quiet-period debounce after changes triggers the round.
/// </summary>
public sealed class FallbackWatcher(
    WatchConfig config,
    ChangeJournal journal,
    Pipeline pipeline,
    AgentActivityTracker? agentActivity = null) : IDisposable
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
        Log.Info(Mode switch
        {
            WatchMode.Journal => $"file journal active on {_watchers.Count} path(s) — scripted edits are detected; rounds still trigger on Claude Stop",
            WatchMode.Hybrid => $"hybrid file watch active on {_watchers.Count} path(s) — editor edits trigger after {config.FallbackWatch.QuietPeriodSec}s quiet, held while the agent is mid-turn",
            _ => $"fallback file watch active on {_watchers.Count} path(s), quiet period {config.FallbackWatch.QuietPeriodSec}s",
        });
    }

    private enum WatchMode { Journal, Hybrid, Trigger }

    private WatchMode Mode => config.FallbackWatch.Mode.ToLowerInvariant() switch
    {
        "journal" => WatchMode.Journal,
        "hybrid" => WatchMode.Hybrid,
        _ => WatchMode.Trigger,
    };

    private void OnChange(string fullPath)
    {
        if (Directory.Exists(fullPath)) return; // directory events (deleted files pass through)
        var normalized = Globs.Normalize(fullPath, config.RepoRoot);
        if (Globs.MatchesAny(normalized, config.Classify.Exclude)) return; // critical: App_Data etc. must not self-trigger

        journal.Add(fullPath);
        if (Mode == WatchMode.Journal) return; // Stop hook decides when to rebuild

        lock (_lock)
        {
            // restart the quiet-period countdown on every relevant change
            _debounce?.Dispose();
            _debounce = new Timer(_ => OnQuietPeriodElapsed(),
                null, TimeSpan.FromSeconds(config.FallbackWatch.QuietPeriodSec), Timeout.InfiniteTimeSpan);
        }
    }

    private bool _holdLogged;

    private void OnQuietPeriodElapsed()
    {
        if (Mode == WatchMode.Hybrid && agentActivity is not null &&
            agentActivity.IsBusy(TimeSpan.FromSeconds(config.FallbackWatch.AgentIdleTimeoutSec)))
        {
            // A Claude turn is in flight: hold the trigger — its Stop-hook round will pick up
            // the journaled edits. Re-check in case the turn is interrupted (no Stop fires);
            // the idle-timeout staleness eventually lets the trigger through.
            if (!_holdLogged)
            {
                _holdLogged = true;
                Log.Detail("editor changes journaled — agent is mid-turn, holding trigger until it finishes");
            }
            lock (_lock)
            {
                _debounce?.Dispose();
                _debounce = new Timer(_ => OnQuietPeriodElapsed(),
                    null, TimeSpan.FromSeconds(config.FallbackWatch.QuietPeriodSec), Timeout.InfiniteTimeSpan);
            }
            return;
        }
        _holdLogged = false;
        pipeline.Post(new Trigger(TriggerKind.FallbackWatch));
    }

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        lock (_lock) _debounce?.Dispose();
    }
}
