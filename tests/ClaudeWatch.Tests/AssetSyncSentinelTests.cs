using ClaudeWatch;
using Xunit;

namespace ClaudeWatch.Tests;

public class AssetSyncSentinelTests
{
    private static (WatchConfig Config, string OutputPath) Setup()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), "cw-sentinel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repoRoot);
        var outputPath = Path.Combine(repoRoot, "app.css");
        File.WriteAllText(outputPath, "/* built */");
        var config = new WatchConfig
        {
            RepoRoot = repoRoot,
            PreBuildSteps = [new WatchConfig.PreBuildStep { Name = "tailwind", Output = "app.css" }],
        };
        return (config, outputPath);
    }

    [Fact]
    public void Unchanged_output_is_not_stale()
    {
        var (config, _) = Setup();
        using var sentinel = new AssetSyncSentinel(config);
        sentinel.CaptureAfterBuild();
        sentinel.Check();
        Assert.Empty(sentinel.StaleFiles);
    }

    [Fact]
    public void Rewrite_after_build_is_flagged_stale()
    {
        var (config, outputPath) = Setup();
        using var sentinel = new AssetSyncSentinel(config);
        sentinel.CaptureAfterBuild();

        // simulate a stray `tailwind --watch` rewriting the file after dotnet build
        File.WriteAllText(outputPath, "/* rewritten by ui:dev — different bytes */");
        File.SetLastWriteTimeUtc(outputPath, DateTime.UtcNow.AddSeconds(5));

        sentinel.Check();
        Assert.Equal([outputPath], sentinel.StaleFiles);
    }

    [Fact]
    public void Next_build_capture_clears_staleness()
    {
        var (config, outputPath) = Setup();
        using var sentinel = new AssetSyncSentinel(config);
        sentinel.CaptureAfterBuild();
        File.WriteAllText(outputPath, "/* rewritten */");
        File.SetLastWriteTimeUtc(outputPath, DateTime.UtcNow.AddSeconds(5));
        sentinel.Check();
        Assert.NotEmpty(sentinel.StaleFiles);

        sentinel.CaptureAfterBuild(); // the next round rebuilt — fingerprints match again
        sentinel.Check();
        Assert.Empty(sentinel.StaleFiles);
    }

    [Fact]
    public void Steps_without_output_are_ignored()
    {
        var (config, _) = Setup();
        config.PreBuildSteps = [new WatchConfig.PreBuildStep { Name = "no-output" }];
        using var sentinel = new AssetSyncSentinel(config);
        sentinel.CaptureAfterBuild();
        sentinel.Check();
        Assert.Empty(sentinel.StaleFiles);
    }
}
