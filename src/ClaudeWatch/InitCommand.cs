using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeWatch;

/// <summary>
/// `claude-watch init [--target <repoDir>] [--port N]` — scaffolds claude-watch.json,
/// hook scripts under .claude/claude-watch/, and creates/merges .claude/settings.json.
/// Never overwrites existing files; prints manual-merge instructions instead.
/// </summary>
public static class InitCommand
{
    public static int Run(string targetDir, int port)
    {
        var target = Path.GetFullPath(targetDir);
        if (!Directory.Exists(target))
        {
            Console.Error.WriteLine($"Target directory does not exist: {target}");
            return 1;
        }
        Console.WriteLine($"Initializing claude-watch in {target}");

        WriteConfigTemplate(target, port);
        WriteHookScripts(target, port);
        WriteHooksSettings(target, port);
        PrintRazorSnippet();

        Console.WriteLine();
        Console.WriteLine("Done. Next steps:");
        Console.WriteLine("  1. Edit claude-watch.json (build/run commands, readiness, exclusions).");
        Console.WriteLine("  2. Add the dev-reload snippet above to your root component (e.g. Components/App.razor).");
        Console.WriteLine("  3. Run: claude-watch   (from the repo, or with --config <path>)");
        return 0;
    }

    private static void WriteConfigTemplate(string target, int port)
    {
        var configPath = Path.Combine(target, "claude-watch.json");
        if (File.Exists(configPath))
        {
            Console.WriteLine($"  = claude-watch.json already exists — left untouched");
            return;
        }
        var template = new WatchConfig
        {
            Name = Path.GetFileName(target.TrimEnd(Path.DirectorySeparatorChar)),
            RepoRoot = ".",
            Server = new WatchConfig.ServerConfig { Port = port },
            Build = new WatchConfig.CommandConfig
            {
                Command = "dotnet",
                Args = ["build", "-v", "q", "--nologo"],
                WorkingDir = ".",
            },
            Run = new WatchConfig.RunConfig
            {
                Command = "dotnet",
                Args = ["run", "--project", "TODO-your-web-project", "--no-build"],
                WorkingDir = ".",
                Env = new Dictionary<string, string> { ["ASPNETCORE_ENVIRONMENT"] = "Development" },
                Readiness = new WatchConfig.ReadinessConfig
                {
                    StdoutRegex = "Now listening on:|Application is running",
                    ProbeUrl = "https://localhost:5001",
                    TimeoutSec = 60,
                },
                AppUrl = "https://localhost:5001",
            },
            Classify = new WatchConfig.ClassifyConfig
            {
                Exclude =
                [
                    "**/bin/**", "**/obj/**", "**/node_modules/**", "**/App_Data/**",
                    "publish/**", ".claude/**", "**/*.md",
                ],
            },
        };
        File.WriteAllText(configPath, template.Serialize());
        Console.WriteLine($"  + claude-watch.json (template — edit the TODOs)");
    }

    private static void WriteHookScripts(string target, int port)
    {
        var hookDir = Path.Combine(target, ".claude", "claude-watch");
        Directory.CreateDirectory(hookDir);
        foreach (var name in new[] { "notify-stop.ps1", "notify-changed.ps1", "notify-stop.sh", "notify-changed.sh" })
        {
            var dest = Path.Combine(hookDir, name);
            if (File.Exists(dest))
            {
                Console.WriteLine($"  = .claude/claude-watch/{name} already exists — left untouched");
                continue;
            }
            var content = TriggerServer.ReadEmbedded(name).Replace("__PORT__", port.ToString());
            File.WriteAllText(dest, content);
            Console.WriteLine($"  + .claude/claude-watch/{name}");
        }
    }

    public static JsonObject BuildHooksJson(int port) => new()
    {
        ["PostToolUse"] = new JsonArray(new JsonObject
        {
            ["matcher"] = "Edit|Write|MultiEdit|NotebookEdit",
            ["hooks"] = new JsonArray(new JsonObject
            {
                ["type"] = "command",
                ["command"] = $"curl -s -m 1 -X POST --data-binary @- http://127.0.0.1:{port}/hook/post-tool-use >/dev/null 2>&1 || true",
                ["async"] = true,
                ["timeout"] = 5,
            }),
        }),
        ["Stop"] = new JsonArray(new JsonObject
        {
            ["hooks"] = new JsonArray(new JsonObject
            {
                ["type"] = "command",
                ["command"] = $"curl -s -m 2 -X POST --data-binary @- http://127.0.0.1:{port}/hook/stop >/dev/null 2>&1 || true",
                ["async"] = true,
                ["timeout"] = 10,
            }),
        }),
    };

    private static void WriteHooksSettings(string target, int port)
    {
        var settingsPath = Path.Combine(target, ".claude", "settings.json");
        var hooks = BuildHooksJson(port);
        var writeOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep `>` readable in hook commands
        };

        if (!File.Exists(settingsPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            var settings = new JsonObject { ["hooks"] = hooks };
            File.WriteAllText(settingsPath, settings.ToJsonString(writeOptions));
            Console.WriteLine($"  + .claude/settings.json (hooks)");
            return;
        }

        var existing = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject;
        if (existing is null)
        {
            Console.WriteLine($"  ! .claude/settings.json is not valid JSON — merge these hooks manually:");
            Console.WriteLine(hooks.ToJsonString(writeOptions));
            return;
        }
        if (existing.ContainsKey("hooks"))
        {
            Console.WriteLine($"  ! .claude/settings.json already defines hooks — merge this block into the existing arrays manually:");
            Console.WriteLine(new JsonObject { ["hooks"] = hooks }.ToJsonString(writeOptions));
            return;
        }
        existing["hooks"] = hooks;
        File.WriteAllText(settingsPath, existing.ToJsonString(writeOptions));
        Console.WriteLine($"  ~ .claude/settings.json (added hooks; other keys preserved)");
    }

    private static void PrintRazorSnippet()
    {
        Console.WriteLine();
        Console.WriteLine("Add this dev-only snippet before </body> in your root component (e.g. Components/App.razor):");
        Console.WriteLine("""

            @* claude-watch: dev reload — inert unless launched by claude-watch *@
            @if (Environment.GetEnvironmentVariable("CLAUDE_WATCH_PORT") is { Length: > 0 } cwPort)
            {
                <script src="@($"http://127.0.0.1:{cwPort}/claude-watch-reload.js")"></script>
            }
            """);
    }
}
