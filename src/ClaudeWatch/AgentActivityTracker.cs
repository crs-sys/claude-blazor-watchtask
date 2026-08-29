namespace ClaudeWatch;

/// <summary>
/// Tracks whether a Claude Code turn is in flight, fed by hooks: UserPromptSubmit → busy,
/// every PostToolUse → activity refresh, Stop → idle. Used by hybrid file-watch mode to hold
/// editor-edit triggers until the agent finishes its turn (the Stop-triggered round then picks
/// the edits up from the shared journal). The staleness window covers interrupted turns,
/// where the Stop hook never fires.
/// </summary>
public sealed class AgentActivityTracker
{
    private readonly Lock _lock = new();
    private bool _busy;
    private DateTime _lastHookUtc = DateTime.MinValue;

    public void MarkBusy()
    {
        lock (_lock) { _busy = true; _lastHookUtc = DateTime.UtcNow; }
    }

    public void MarkActivity()
    {
        lock (_lock) { _busy = true; _lastHookUtc = DateTime.UtcNow; }
    }

    public void MarkIdle()
    {
        lock (_lock) { _busy = false; _lastHookUtc = DateTime.UtcNow; }
    }

    /// <summary>Busy, unless no hook traffic for <paramref name="staleness"/> (interrupted turn).</summary>
    public bool IsBusy(TimeSpan staleness)
    {
        lock (_lock) return _busy && DateTime.UtcNow - _lastHookUtc < staleness;
    }

    public bool RawBusyFlag { get { lock (_lock) return _busy; } }
}
