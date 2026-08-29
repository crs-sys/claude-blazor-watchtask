namespace ClaudeWatch;

public enum TriggerKind { ClaudeStop, Manual, Api, FallbackWatch }

public sealed record Trigger(TriggerKind Kind, string? SessionId = null);

public enum PlanKind { Skip, CssOnly, Full }

public sealed record RoundPlan(PlanKind Kind, IReadOnlyList<string> Files);

/// <summary>
/// Thread-safe set of files Claude (or the fallback watcher) has touched since the last round.
/// Paths are stored normalized (forward slashes, relative to repoRoot).
/// </summary>
public sealed class ChangeJournal
{
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();
    private readonly string _repoRoot;

    public ChangeJournal(string repoRoot) => _repoRoot = repoRoot;

    public void Add(string path)
    {
        string normalized;
        try { normalized = Globs.Normalize(path, _repoRoot); }
        catch { return; } // malformed path from a hook payload — ignore
        // Paths outside the repo (e.g. Claude edited a file elsewhere) are irrelevant
        if (normalized.StartsWith("..", StringComparison.Ordinal)) return;
        lock (_lock) _files.Add(normalized);
    }

    public IReadOnlyList<string> Drain()
    {
        lock (_lock)
        {
            var snapshot = _files.ToList();
            _files.Clear();
            return snapshot;
        }
    }

    /// <summary>Put a snapshot back (used after a failed build so the retry is never classified Skip).</summary>
    public void Restore(IEnumerable<string> files)
    {
        lock (_lock)
        {
            foreach (var f in files) _files.Add(f);
        }
    }

    public IReadOnlyList<string> Peek()
    {
        lock (_lock) return _files.ToList();
    }
}

public static class Classifier
{
    public static RoundPlan Plan(IReadOnlyList<string> files, Trigger trigger, WatchConfig.ClassifyConfig config)
    {
        // Manual/hotkey triggers force a full round regardless of what the journal holds.
        if (trigger.Kind == TriggerKind.Manual)
            return new RoundPlan(PlanKind.Full, files);

        var relevant = files.Where(f => !Globs.MatchesAny(f, config.Exclude)).ToList();
        if (relevant.Count == 0)
            return new RoundPlan(PlanKind.Skip, relevant);

        if (config.CssFastPath &&
            config.CssOnly.Count > 0 &&
            relevant.All(f => Globs.MatchesAny(f, config.CssOnly)))
        {
            return new RoundPlan(PlanKind.CssOnly, relevant);
        }

        return new RoundPlan(PlanKind.Full, relevant);
    }
}
