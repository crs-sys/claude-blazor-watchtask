using System.Diagnostics;
using System.Threading.Channels;

namespace ClaudeWatch;

public enum WatchState { Starting, Ready, Building, Restarting, BuildFailed, AppCrashed, ShuttingDown }

/// <summary>
/// The single consumer of the trigger channel. Owns all app/build state transitions,
/// so overlapping triggers can never interleave pipeline stages.
/// </summary>
public sealed class Pipeline(
    WatchConfig config,
    ChangeJournal journal,
    IStepRunner stepRunner,
    IBuildRunner buildRunner,
    IAppSupervisor supervisor,
    IReloadBroadcaster reloadBroadcaster,
    AssetSyncSentinel? sentinel = null)
{
    private readonly Channel<Trigger> _triggers = Channel.CreateUnbounded<Trigger>();

    public WatchState State { get; private set; } = WatchState.Starting;
    public int Round { get; private set; }
    public BuildResult? LastBuild { get; private set; }
    public int LastRoundFileCount { get; private set; }
    public bool LastRoundSkipped { get; private set; }

    public void Post(Trigger trigger) => _triggers.Writer.TryWrite(trigger);

    public async Task RunAsync(CancellationToken ct)
    {
        // Initial round: build + start so the session begins with a running app
        await ExecuteRoundAsync(new RoundPlan(PlanKind.Full, []), new Trigger(TriggerKind.Manual), ct);

        await foreach (var first in _triggers.Reader.ReadAllAsync(ct))
        {
            // Coalesce bursts: N rapid Stops => one round. A Manual trigger anywhere in the burst wins.
            var trigger = first;
            while (_triggers.Reader.TryRead(out var extra))
                if (extra.Kind == TriggerKind.Manual) trigger = extra;

            var snapshot = journal.Drain();
            var plan = Classifier.Plan(snapshot, trigger, config.Classify);
            Round++;

            if (plan.Kind == PlanKind.Skip)
            {
                LastRoundSkipped = true;
                LastRoundFileCount = 0;
                Log.Detail($"round {Round}: no relevant changes — skipped");
                continue;
            }

            LastRoundSkipped = false;
            LastRoundFileCount = plan.Files.Count;
            var fileSummary = plan.Files.Count == 0 ? "forced"
                : string.Join(", ", plan.Files.Take(3).Select(Path.GetFileName)) +
                  (plan.Files.Count > 3 ? $", +{plan.Files.Count - 3}" : "");
            Log.Info($"round {Round}  trigger={TriggerName(trigger)}  files={plan.Files.Count} ({fileSummary})");

            var ok = await ExecuteRoundAsync(plan, trigger, ct);
            if (!ok && plan.Files.Count > 0)
                journal.Restore(plan.Files); // retry must never classify as Skip
        }
    }

    private async Task<bool> ExecuteRoundAsync(RoundPlan plan, Trigger trigger, CancellationToken ct)
    {
        var roundWatch = Stopwatch.StartNew();

        if (plan.Kind == PlanKind.CssOnly)
        {
            // Fast path (opt-in via classify.cssFastPath): rebuild CSS, reload browser, no restart
            State = WatchState.Building;
            if (!await RunMatchingStepsAsync(plan, forceAll: false, ct)) { State = WatchState.BuildFailed; return false; }
            State = supervisor.IsRunning ? WatchState.Ready : State;
            var count = reloadBroadcaster.Broadcast();
            Log.Success($"READY  round {Round} css-only in {roundWatch.Elapsed.TotalSeconds:0.0}s — reload sent ({count} client{(count == 1 ? "" : "s")})");
            return true;
        }

        // Full round: stop BEFORE build — the running app locks its output DLLs
        State = WatchState.Building;
        if (supervisor.IsRunning)
        {
            Log.Detail($"stopping app (pid {supervisor.Pid})...");
            await supervisor.StopAsync();
        }

        var forceAllSteps = trigger.Kind == TriggerKind.Manual;
        if (!await RunMatchingStepsAsync(plan, forceAllSteps, ct))
        {
            State = WatchState.BuildFailed;
            Log.Error("BUILD FAILED (pre-build step) — app is DOWN — waiting for next trigger (R to retry)");
            return false;
        }

        Log.Detail("dotnet build...");
        var build = await buildRunner.BuildAsync(ct);
        LastBuild = build;
        if (!build.Success)
        {
            State = WatchState.BuildFailed;
            Log.Error($"build failed in {build.Duration.TotalSeconds:0.0}s:");
            if (build.Errors.Count > 0)
                foreach (var e in build.Errors.Take(15))
                    Log.Error($"  {e.File}({e.Line}): {e.Code} {e.Message}");
            else
                foreach (var line in build.OutputTail) Log.App(line);
            Log.Error("BUILD FAILED — app is DOWN — waiting for next trigger (R to retry)");
            return false;
        }
        Log.Detail($"build OK in {build.Duration.TotalSeconds:0.0}s");
        sentinel?.CaptureAfterBuild();

        State = WatchState.Restarting;
        Log.Detail("starting app...");
        var startWatch = Stopwatch.StartNew();
        var failure = await supervisor.StartAsync(ct);
        if (failure is not null)
        {
            State = WatchState.AppCrashed;
            Log.Error($"app failed to start: {failure}");
            foreach (var line in supervisor is AppSupervisor s ? s.OutputTail : []) Log.App(line);
            return false;
        }

        State = WatchState.Ready;
        var clients = config.BrowserReload.Enabled ? reloadBroadcaster.Broadcast() : 0;
        Log.Success(
            $"READY  round {Round} done in {roundWatch.Elapsed.TotalSeconds:0.0}s " +
            $"(app up in {startWatch.Elapsed.TotalSeconds:0.0}s, pid {supervisor.Pid}" +
            (clients > 0 ? $", reload sent to {clients} client{(clients == 1 ? "" : "s")})" : ")"));
        return true;
    }

    private async Task<bool> RunMatchingStepsAsync(RoundPlan plan, bool forceAll, CancellationToken ct)
    {
        foreach (var step in config.PreBuildSteps)
        {
            var applies = forceAll
                          || step.When.Count == 0
                          || plan.Files.Any(f => Globs.MatchesAny(f, step.When));
            if (!applies)
            {
                Log.Detail($"step '{step.Name}' skipped (no matching files)");
                continue;
            }
            Log.Detail($"step '{step.Name}'...");
            var stepWatch = Stopwatch.StartNew();
            if (!await stepRunner.RunAsync(step, ct)) return false;
            Log.Detail($"step '{step.Name}' done in {stepWatch.Elapsed.TotalSeconds:0.0}s");
        }
        return true;
    }

    private static string TriggerName(Trigger t) => t.Kind switch
    {
        TriggerKind.ClaudeStop => "claude-stop",
        TriggerKind.Manual => "manual",
        TriggerKind.Api => "api",
        TriggerKind.FallbackWatch => "file-watch",
        _ => t.Kind.ToString(),
    };
}
