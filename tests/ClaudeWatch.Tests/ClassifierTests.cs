using ClaudeWatch;
using Xunit;

namespace ClaudeWatch.Tests;

public class GlobTests
{
    [Theory]
    [InlineData("Sra/App_Data/logs.db", "**/App_Data/**", true)]
    [InlineData("App_Data/logs.db", "**/App_Data/**", true)]
    [InlineData("Sra/bin/Debug/net10.0/Sra.dll", "**/bin/**", true)]
    [InlineData("Sra/Components/Pages/Home.razor", "**/*.razor", true)]
    [InlineData("Sra/wwwroot/css/app.css", "Sra/wwwroot/css/app.css", true)]
    [InlineData("SRA/WWWROOT/CSS/APP.CSS", "Sra/wwwroot/css/app.css", true)] // case-insensitive
    [InlineData("Sra/Services/FooService.cs", "**/*.razor", false)]
    [InlineData("Sra/tailwind.input.css", "Sra/tailwind.input.css", true)]
    [InlineData("README.md", "**/*.md", true)]
    [InlineData("docs/notes.md", "docs/**", true)]
    [InlineData("Sra/.playwright-cli/state.json", "**/.playwright-*/**", true)]
    [InlineData("Sra/binocular.cs", "**/bin/**", false)] // "bin" must be a whole segment
    public void Glob_matching(string path, string glob, bool expected) =>
        Assert.Equal(expected, Globs.IsMatch(path, glob));

    [Fact]
    public void Normalize_produces_forward_slash_relative_paths()
    {
        var repoRoot = Path.GetTempPath();
        var normalized = Globs.Normalize(Path.Combine(repoRoot, "Sra", "Foo.cs"), repoRoot);
        Assert.Equal("Sra/Foo.cs", normalized);
    }
}

public class ClassifierTests
{
    private static readonly WatchConfig.ClassifyConfig Config = new()
    {
        Exclude = ["**/App_Data/**", "**/bin/**", "**/obj/**", "**/*.md", ".claude/**"],
        CssOnly = ["Sra/tailwind.input.css", "Sra/wwwroot/**"],
        CssFastPath = false,
    };

    private static readonly Trigger Stop = new(TriggerKind.ClaudeStop);

    [Fact]
    public void Empty_round_is_skip() =>
        Assert.Equal(PlanKind.Skip, Classifier.Plan([], Stop, Config).Kind);

    [Fact]
    public void Only_excluded_files_is_skip()
    {
        var plan = Classifier.Plan(["README.md", "Sra/App_Data/logs.db", ".claude/settings.json"], Stop, Config);
        Assert.Equal(PlanKind.Skip, plan.Kind);
    }

    [Fact]
    public void Code_change_is_full()
    {
        var plan = Classifier.Plan(["README.md", "Sra/Services/FooService.cs"], Stop, Config);
        Assert.Equal(PlanKind.Full, plan.Kind);
        Assert.Equal(["Sra/Services/FooService.cs"], plan.Files);
    }

    [Fact]
    public void Css_only_routes_to_full_when_fast_path_disabled()
    {
        var plan = Classifier.Plan(["Sra/tailwind.input.css"], Stop, Config);
        Assert.Equal(PlanKind.Full, plan.Kind);
    }

    [Fact]
    public void Css_only_fast_path_when_enabled()
    {
        var fastConfig = new WatchConfig.ClassifyConfig
        {
            Exclude = Config.Exclude,
            CssOnly = Config.CssOnly,
            CssFastPath = true,
        };
        var plan = Classifier.Plan(["Sra/tailwind.input.css", "Sra/wwwroot/img/logo.png"], Stop, fastConfig);
        Assert.Equal(PlanKind.CssOnly, plan.Kind);

        var mixed = Classifier.Plan(["Sra/tailwind.input.css", "Sra/Foo.cs"], Stop, fastConfig);
        Assert.Equal(PlanKind.Full, mixed.Kind);
    }

    [Fact]
    public void Manual_trigger_forces_full_even_with_empty_journal() =>
        Assert.Equal(PlanKind.Full, Classifier.Plan([], new Trigger(TriggerKind.Manual), Config).Kind);
}

public class ChangeJournalTests
{
    [Fact]
    public void Add_drain_restore_cycle()
    {
        var repoRoot = Path.GetTempPath();
        var journal = new ChangeJournal(repoRoot);
        journal.Add(Path.Combine(repoRoot, "Sra", "A.cs"));
        journal.Add(Path.Combine(repoRoot, "Sra", "A.cs")); // dedup
        journal.Add(Path.Combine(repoRoot, "Sra", "B.razor"));

        var snapshot = journal.Drain();
        Assert.Equal(2, snapshot.Count);
        Assert.Empty(journal.Peek());

        journal.Restore(snapshot);
        Assert.Equal(2, journal.Peek().Count);
    }

    [Fact]
    public void Paths_outside_repo_are_ignored()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), "repo-a");
        var journal = new ChangeJournal(repoRoot);
        journal.Add(Path.Combine(Path.GetTempPath(), "elsewhere", "X.cs"));
        Assert.Empty(journal.Peek());
    }
}
