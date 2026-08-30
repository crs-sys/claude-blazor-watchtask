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
        public int CssBroadcasts;
        public int BuildingBroadcasts;
        public List<BuildError> ErrorsBroadcast = [];
        public int Broadcast() { Broadcasts++; return 1; }
        public int BroadcastCssUpdate(string route, string url) { CssBroadcasts++; return 1; }
        public int BroadcastBuilding(int round) { BuildingBroadcasts++; return 1; }
        public int BroadcastBuildError(int round, IEnumerable<BuildError> errors)
        {
            ErrorsBroadcast.AddRange(errors);
            return 1;
        }
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

    // Timings are assigned in a finally after the round's counters tick — poll for them separately
    private static async Task WaitForTimingsAsync(Pipeline pipeline, int round)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (pipeline.LastRoundTimings?.Round != round && DateTime.UtcNow < deadline)
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
    public async Task Css_only_round_hot_swaps_without_restart_and_full_round_clears_override()
    {
        var config = TestConfig();
        config.Classify.CssOnly = ["tailwind.input.css"];
        config.Classify.CssFastPath = true;
        config.PreBuildSteps =
        [
            new WatchConfig.PreBuildStep
            {
                Name = "tailwind", Output = "wwwroot/css/app.css", Route = "css/app.css",
                When = ["tailwind.input.css", "**/*.razor"],
            },
        ];
        var journal = new ChangeJournal(config.RepoRoot);
        var builds = new FakeBuildRunner();
        var supervisor = new FakeSupervisor();
        var broadcaster = new FakeBroadcaster();
        var overrides = new AssetOverrideStore();
        var pipeline = new Pipeline(config, journal, new FakeStepRunner(), builds, supervisor, broadcaster,
            sentinel: null, overrides);

        using var cts = new CancellationTokenSource();
        var run = pipeline.RunAsync(cts.Token);
        await DrainRoundsAsync(pipeline, builds, expectedBuilds: 1); // initial full round

        journal.Add(Path.Combine(config.RepoRoot, "tailwind.input.css"));
        pipeline.Post(new Trigger(TriggerKind.ClaudeStop));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (broadcaster.CssBroadcasts < 1 && DateTime.UtcNow < deadline) await Task.Delay(10);

        Assert.Equal(1, builds.Builds);                 // css round: no dotnet build
        Assert.Equal(1, supervisor.Starts);             // ...and no restart
        Assert.Equal(1, broadcaster.CssBroadcasts);
        Assert.Equal(["css/app.css"], overrides.Routes);
        Assert.Equal(WatchState.Ready, pipeline.State);

        await WaitForTimingsAsync(pipeline, round: 1);
        Assert.Equal("css-only", pipeline.LastRoundTimings!.Kind);
        Assert.True(pipeline.LastRoundTimings.Succeeded);
        Assert.Contains(pipeline.LastRoundTimings.Phases, p => p.Name == "tailwind");
        Assert.DoesNotContain(pipeline.LastRoundTimings.Phases, p => p.Name is "build" or "start" or "stop");

        journal.Add(Path.Combine(config.RepoRoot, "Foo.cs"));
        pipeline.Post(new Trigger(TriggerKind.ClaudeStop));
        await DrainRoundsAsync(pipeline, builds, expectedBuilds: 2); // full round
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Empty(overrides.Routes);                 // full round cleared the override
        Assert.Equal(2, supervisor.Starts);
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

    [Fact]
    public async Task Round_timings_record_phase_breakdown_for_success_and_failure()
    {
        var config = TestConfig();
        var journal = new ChangeJournal(config.RepoRoot);
        var builds = new FakeBuildRunner();
        builds.Outcomes.Enqueue(true);   // initial round
        builds.Outcomes.Enqueue(false);  // round 1 fails
        var supervisor = new FakeSupervisor();
        var pipeline = new Pipeline(config, journal, new FakeStepRunner(), builds, supervisor, new FakeBroadcaster());

        using var cts = new CancellationTokenSource();
        var run = pipeline.RunAsync(cts.Token);
        await DrainRoundsAsync(pipeline, builds, expectedBuilds: 1);
        await WaitForTimingsAsync(pipeline, round: 0);

        var initial = pipeline.LastRoundTimings!;
        Assert.Equal("full", initial.Kind);
        Assert.True(initial.Succeeded);
        Assert.Contains(initial.Phases, p => p.Name == "build");
        Assert.Contains(initial.Phases, p => p.Name == "start");
        Assert.DoesNotContain(initial.Phases, p => p.Name == "stop"); // nothing was running yet

        journal.Add(Path.Combine(config.RepoRoot, "Foo.cs"));
        pipeline.Post(new Trigger(TriggerKind.ClaudeStop));
        await DrainRoundsAsync(pipeline, builds, expectedBuilds: 2);
        await WaitForTimingsAsync(pipeline, round: 1);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        var failed = pipeline.LastRoundTimings!;
        Assert.Equal(1, failed.Round);
        Assert.False(failed.Succeeded);
        Assert.Contains(failed.Phases, p => p.Name == "stop");   // app was up from the initial round
        Assert.Contains(failed.Phases, p => p.Name == "build");  // where the failure happened
        Assert.DoesNotContain(failed.Phases, p => p.Name == "start");
    }
}
