using ClaudeWatch;

var (verb, options) = ParseArgs(args);

switch (verb)
{
    case "init":
        return InitCommand.Run(
            options.GetValueOrDefault("--target", Directory.GetCurrentDirectory()),
            int.Parse(options.GetValueOrDefault("--port", "43617")));

    case "status":
        return await StatusCommand.RunAsync(int.Parse(options.GetValueOrDefault("--port", "43617")));

    case "run":
        return await RunWatcherAsync(options);

    default:
        Console.Error.WriteLine("""
            claude-watch — Claude Code-aware alternative to dotnet watch

            Usage:
              claude-watch [--config <claude-watch.json>] [--watch]   run the watcher
              claude-watch init [--target <repoDir>] [--port <n>]     scaffold config + hooks into a repo
              claude-watch status [--port <n>]                        query a running watcher

            Hotkeys while running:  R rebuild   S status   C clear   Q quit
            """);
        return verb == "help" ? 0 : 1;
}

static async Task<int> RunWatcherAsync(Dictionary<string, string> options)
{
    var configPath = options.GetValueOrDefault("--config")
                     ?? WatchConfig.FindConfig(Directory.GetCurrentDirectory());
    if (configPath is null)
    {
        Console.Error.WriteLine("No claude-watch.json found here or in any parent directory. Use --config or run `claude-watch init`.");
        return 1;
    }

    WatchConfig config;
    try { config = WatchConfig.Load(configPath); }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Config error: {ex.Message}");
        return 1;
    }

    Console.WriteLine($"claude-watch  •  {config.Name}  •  http://127.0.0.1:{config.Server.Port}  •  app: {config.Run.AppUrl ?? "(unknown url)"}");
    Console.WriteLine($"config: {config.ConfigPath}");

    using var shutdown = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };

    var journal = new ChangeJournal(config.RepoRoot);
    var reloadService = new BrowserReloadService();
    using var supervisor = new AppSupervisor(config);
    using var sentinel = new AssetSyncSentinel(config);
    var pipeline = new Pipeline(config, journal, new StepRunner(config), new BuildRunner(config), supervisor, reloadService, sentinel);
    var server = new TriggerServer(config, journal, pipeline, supervisor, reloadService, sentinel);

    try { await server.StartAsync(shutdown.Token); }
    catch (Exception ex) when (ex is IOException or InvalidOperationException)
    {
        Console.Error.WriteLine($"Cannot listen on port {config.Server.Port} ({ex.Message}). Is another claude-watch running? Override with server.port in config.");
        return 1;
    }

    using var fallback = config.FallbackWatch.Enabled || options.ContainsKey("--watch")
        ? new FallbackWatcher(config, journal, pipeline)
        : null;
    fallback?.Start();

    var ui = new ConsoleUi(pipeline, server, shutdown);
    var uiThread = new Thread(ui.RunKeyLoop) { IsBackground = true };
    uiThread.Start();

    // Take ownership of the app's ports: an orphaned instance from a previous session would
    // hold the port AND lock the build output DLLs.
    OrphanKiller.KillListenersOn(config.Run.KillOrphansOnPorts);

    try
    {
        await pipeline.RunAsync(shutdown.Token);
    }
    catch (OperationCanceledException) { }
    finally
    {
        Log.Info("stopping app...");
        await supervisor.StopAsync();
        await server.StopAsync();
    }
    return 0;
}

static (string Verb, Dictionary<string, string> Options) ParseArgs(string[] args)
{
    var verb = "run";
    var options = new Dictionary<string, string>();
    var i = 0;
    if (args.Length > 0 && !args[0].StartsWith('-'))
    {
        verb = args[0].ToLowerInvariant();
        if (verb is not ("init" or "status" or "run" or "help")) verb = "unknown";
        i = 1;
    }
    for (; i < args.Length; i++)
    {
        if (args[i] is "--help" or "-h") return ("help", options);
        if (args[i] == "--watch") { options["--watch"] = "true"; continue; }
        if (args[i].StartsWith("--") && i + 1 < args.Length)
        {
            options[args[i]] = args[i + 1];
            i++;
        }
    }
    return (verb, options);
}
