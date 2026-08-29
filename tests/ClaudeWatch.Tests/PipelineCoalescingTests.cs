using ClaudeWatch;
using Xunit;

namespace ClaudeWatch.Tests;

public class PipelineCoalescingTests
{
    private sealed class FakeStepRunner : IStepRunner
    {
        public int Runs;
        public Task<bool> RunAsync(WatchConfig.PreBuildStep step, CancellationToken ct)
        {
            Interlocked.Increment(ref Runs);
            return Task.FromResult(true);
        }
    }

    private sealed class FakeBuildRunner : IBuildRunner
    {
        public int Builds;
        public Queue<bool> Outcomes = new();
        public Task<BuildResult> BuildAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Builds);
            var success = Outcomes.Count == 0 || Outcomes.Dequeue();
            return Task.FromResult(new BuildResult(success, TimeSpan.FromMilliseconds(1), [], [], DateTimeOffset.Now));
        }
    }

    private sealed class FakeSupervisor : IAppSupervisor
    {
        public int Starts, Stops;
        public bool IsRunning { get; private set; }
        public int? Pid => IsRunning ? 12345 : null;
        public DateTimeOffset? StartedAt { get; private set; }
        public Task StopAsync() { if (IsRunning) Stops++; IsRunning = false; return Task.CompletedTask; }
        public Task<string?> StartAsync(CancellationToken ct)
        {
            Starts++; IsRunning = true; StartedAt = DateTimeOffset.Now;
            return Task.FromResult<string?>(null);
        }
    }

    private sealed class FakeBroadcaster : IReloadBroadcaster
    {
        public int Broadcasts;
        public int Broadcast() { Broadcasts++; return 1; }
    }

    private static WatchConfig TestConfig() => new()
    {
        RepoRoot = Path.GetTempPath(),
        Classify = new WatchConfig.ClassifyConfig { Exclude = ["**/*.md"] },
    };

    private static async Task DrainRoundsAsync(Pipeline pipeline, FakeBuildRunner builds, int expectedBuilds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (builds.Builds < expectedBuilds && DateTime.UtcNow < deadline)
            await Task.Delay(10);
    }

    [Fact]
    public async Task Burst_of_stop_triggers_coalesces_into_one_round()
    {
        var config = TestConfig();
        var journal = new ChangeJournal(config.RepoRoot);
        var builds = new FakeBuildRunner();
        var supervisor = new FakeSupervisor();
        var broadcaster = new FakeBroadcaster();
        var pipeline = new Pipeline(config, journal, new FakeStepRunner(), builds, supervisor, broadcaster);

        journal.Add(Path.Combine(config.RepoRoot, "Foo.cs"));
        for (var i = 0; i < 5; i++) pipeline.Post(new Trigger(TriggerKind.ClaudeStop));

        using var cts = new CancellationTokenSource();
        var run = pipeline.RunAsync(cts.Token);
        await DrainRoundsAsync(pipeline, builds, expectedBuilds: 2); // initial round + one coalesced round
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Equal(2, builds.Builds);
        Assert.Equal(2, supervisor.Starts);
        Assert.Equal(WatchState.Ready, pipeline.State);
    }

    [Fact]
    public async Task Chat_only_turn_skips_rebuild()
    {
        var config = TestConfig();
        var journal = new ChangeJournal(config.RepoRoot);
        var builds = new FakeBuildRunner();
        var pipeline = new Pipeline(config, journal, new FakeStepRunner(), builds, new FakeSupervisor(), new FakeBroadcaster());

        journal.Add(Path.Combine(config.RepoRoot, "README.md")); // excluded
        pipeline.Post(new Trigger(TriggerKind.ClaudeStop));

        using var cts = new CancellationTokenSource();
        var run = pipeline.RunAsync(cts.Token);
        await DrainRoundsAsync(pipeline, builds, expectedBuilds: 1); // initial round only
        await Task.Delay(100); // give the skip round time to be consumed
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Equal(1, builds.Builds);
        Assert.True(pipeline.LastRoundSkipped);
    }

    [Fact]
    public async Task Failed_build_restores_journal_so_retry_is_not_skipped()
    {
        var config = TestConfig();
        var journal = new ChangeJournal(config.RepoRoot);
        var builds = new FakeBuildRunner();
        builds.Outcomes.Enqueue(true);   // initial round
        builds.Outcomes.Enqueue(false);  // round 1 fails
        builds.Outcomes.Enqueue(true);   // retry succeeds
        var supervisor = new FakeSupervisor();
        var pipeline = new Pipeline(config, journal, new FakeStepRunner(), builds, supervisor, new FakeBroadcaster());

        journal.Add(Path.Combine(config.RepoRoot, "Foo.cs"));
        pipeline.Post(new Trigger(TriggerKind.ClaudeStop));

        using var cts = new CancellationTokenSource();
        var run = pipeline.RunAsync(cts.Token);
        await DrainRoundsAsync(pipeline, builds, expectedBuilds: 2);
        Assert.Equal(WatchState.BuildFailed, pipeline.State);

        // journal was restored — a bare retry trigger must rebuild, not skip
        pipeline.Post(new Trigger(TriggerKind.ClaudeStop));
        await DrainRoundsAsync(pipeline, builds, expectedBuilds: 3);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Equal(3, builds.Builds);
        Assert.Equal(WatchState.Ready, pipeline.State);
    }
}
