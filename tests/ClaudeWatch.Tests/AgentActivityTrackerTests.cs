using ClaudeWatch;
using Xunit;

namespace ClaudeWatch.Tests;

public class AgentActivityTrackerTests
{
    private static readonly TimeSpan Staleness = TimeSpan.FromMinutes(3);

    [Fact]
    public void Idle_by_default()
    {
        var tracker = new AgentActivityTracker();
        Assert.False(tracker.IsBusy(Staleness));
    }

    [Fact]
    public void Prompt_marks_busy_and_stop_marks_idle()
    {
        var tracker = new AgentActivityTracker();
        tracker.MarkBusy();
        Assert.True(tracker.IsBusy(Staleness));
        tracker.MarkIdle();
        Assert.False(tracker.IsBusy(Staleness));
    }

    [Fact]
    public void Tool_activity_marks_busy_even_without_prompt_hook()
    {
        // older scaffolds may lack the UserPromptSubmit hook — any tool call still means a turn is in flight
        var tracker = new AgentActivityTracker();
        tracker.MarkActivity();
        Assert.True(tracker.IsBusy(Staleness));
    }

    [Fact]
    public void Interrupted_turn_goes_stale_after_idle_timeout()
    {
        // Stop hook never fires on user interrupt — staleness must eventually release the hold
        var tracker = new AgentActivityTracker();
        tracker.MarkBusy();
        Assert.True(tracker.RawBusyFlag);
        Assert.False(tracker.IsBusy(TimeSpan.Zero)); // zero staleness => any silence counts as idle
        Assert.True(tracker.IsBusy(Staleness));      // ...but within the window it's still busy
    }
}
