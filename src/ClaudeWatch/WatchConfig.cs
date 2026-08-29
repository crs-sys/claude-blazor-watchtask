using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeWatch;

public sealed class WatchConfig
{
    public string Name { get; set; } = "app";
    public string RepoRoot { get; set; } = ".";
    public ServerConfig Server { get; set; } = new();
    public CommandConfig Build { get; set; } = new() { Command = "dotnet", Args = ["build"] };
    public List<PreBuildStep> PreBuildSteps { get; set; } = [];
    public RunConfig Run { get; set; } = new();
    public ClassifyConfig Classify { get; set; } = new();
    public BrowserReloadConfig BrowserReload { get; set; } = new();
    public FallbackWatchConfig FallbackWatch { get; set; } = new();

    [JsonIgnore]
    public string ConfigPath { get; set; } = "";

    public sealed class ServerConfig
    {
        public int Port { get; set; } = 43617;
    }

    public class CommandConfig
    {
        public string Command { get; set; } = "";
        public List<string> Args { get; set; } = [];
        public string WorkingDir { get; set; } = ".";
        public Dictionary<string, string> Env { get; set; } = [];
        public bool InheritEnv { get; set; } = true;
    }

    public sealed class PreBuildStep : CommandConfig
    {
        public string Name { get; set; } = "step";
        /// <summary>Globs (relative to repoRoot); step runs only when a changed file matches. Empty = always run.</summary>
        public List<string> When { get; set; } = [];
        public int TimeoutSec { get; set; } = 120;
        /// <summary>
        /// File this step produces (relative to repoRoot). If anything rewrites it AFTER the round's
        /// build (e.g. a stray `tailwind --watch`), MapStaticAssets' build-time fingerprints no longer
        /// match the file and browsers get broken/stale CSS — the watcher warns loudly when it happens.
        /// </summary>
        public string? Output { get; set; }
        /// <summary>
        /// URL path the app serves the output at (e.g. "css/app.css"). Required for the css fast
        /// path: css-only rounds serve the fresh file from the watcher at /asset/{route} and tell
        /// browser tabs to swap their &lt;link&gt; to it (the app can't serve post-build content
        /// correctly under MapStaticAssets).
        /// </summary>
        public string? Route { get; set; }
    }

    public sealed class RunConfig : CommandConfig
    {
        public List<string> RequiredEnv { get; set; } = [];
        public ReadinessConfig Readiness { get; set; } = new();
        public string? AppUrl { get; set; }
        public List<int> KillOrphansOnPorts { get; set; } = [];
    }

    public sealed class ReadinessConfig
    {
        public string StdoutRegex { get; set; } = "Now listening on:|Application is running";
        public string? ProbeUrl { get; set; }
        public int TimeoutSec { get; set; } = 60;
    }

    public sealed class ClassifyConfig
    {
        public List<string> Exclude { get; set; } = ["**/bin/**", "**/obj/**", "**/node_modules/**", "**/*.md", ".claude/**"];
        public List<string> CssOnly { get; set; } = [];
        public bool CssFastPath { get; set; } = false;
    }

    public sealed class BrowserReloadConfig
    {
        public bool Enabled { get; set; } = true;
    }

    public sealed class FallbackWatchConfig
    {
        public bool Enabled { get; set; } = false;
        /// <summary>
        /// "journal": filesystem changes only feed the change journal — rounds are still
        /// triggered by the Claude Stop hook. Catches edits made by scripts (python, node,
        /// sed, br-edit.js) that bypass the Edit/Write tool hooks.
        /// "hybrid" (recommended alongside hooks): like journal, but editor edits also
        /// self-trigger after the quiet period — held while a Claude turn is in flight, so
        /// the Stop-hook round picks them up instead of rebuilding mid-turn.
        /// "trigger": hook-free mode — a quiet-period debounce after changes triggers the round.
        /// </summary>
        public string Mode { get; set; } = "trigger";
        public List<string> Paths { get; set; } = [];
        public int QuietPeriodSec { get; set; } = 1;
        /// <summary>Hybrid mode: treat the agent as idle after this long without any hook traffic
        /// (covers interrupted turns, where the Stop hook never fires).</summary>
        public int AgentIdleTimeoutSec { get; set; } = 180;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    public static WatchConfig Load(string path)
    {
        var full = Path.GetFullPath(path);
        var config = JsonSerializer.Deserialize<WatchConfig>(File.ReadAllText(full), JsonOptions)
                     ?? throw new InvalidOperationException($"Config file is empty: {full}");
        config.ConfigPath = full;
        // repoRoot is relative to the config file's directory when not absolute
        config.RepoRoot = Path.GetFullPath(config.RepoRoot, Path.GetDirectoryName(full)!);
        config.Validate();
        return config;
    }

    /// <summary>Search for claude-watch.json in <paramref name="startDir"/> and its parents.</summary>
    public static string? FindConfig(string startDir)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(startDir));
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "claude-watch.json");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    public void Validate()
    {
        if (!Directory.Exists(RepoRoot))
            throw new InvalidOperationException($"repoRoot does not exist: {RepoRoot}");
        if (string.IsNullOrWhiteSpace(Build.Command))
            throw new InvalidOperationException("build.command is required");
        if (string.IsNullOrWhiteSpace(Run.Command))
            throw new InvalidOperationException("run.command is required");
        if (Server.Port is < 1 or > 65535)
            throw new InvalidOperationException($"server.port out of range: {Server.Port}");
        foreach (var name in Run.RequiredEnv)
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
                throw new InvalidOperationException(
                    $"Required environment variable '{name}' is not set (run.requiredEnv).");
        }
        if (Classify.CssFastPath && !PreBuildSteps.Any(s => s.Output is { Length: > 0 } && s.Route is { Length: > 0 }))
            throw new InvalidOperationException(
                "classify.cssFastPath requires at least one preBuildSteps entry with both 'output' and 'route' " +
                "(the css fast path serves that file from the watcher).");
    }

    public string ResolvePath(string relative) => Path.GetFullPath(relative, RepoRoot);

    public string Serialize() => JsonSerializer.Serialize(this, JsonOptions);
}
