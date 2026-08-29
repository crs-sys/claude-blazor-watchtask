using ClaudeWatch;
using Xunit;

namespace ClaudeWatch.Tests;

public class AssetOverrideStoreTests
{
    [Fact]
    public void Register_lookup_routes_clear()
    {
        var store = new AssetOverrideStore();
        store.Register("css/app.css", @"C:\repo\wwwroot\css\app.css");

        Assert.True(store.TryGet("css/app.css", out var path));
        Assert.Equal(@"C:\repo\wwwroot\css\app.css", path);
        Assert.True(store.TryGet("CSS/APP.CSS", out _));       // case-insensitive
        Assert.True(store.TryGet("/css/app.css", out _));      // leading slash normalized
        Assert.False(store.TryGet("css/other.css", out _));
        Assert.Equal(["css/app.css"], store.Routes);

        store.Clear();
        Assert.Empty(store.Routes);
        Assert.False(store.TryGet("css/app.css", out _));
    }
}

public class CssFastPathClassifierTests
{
    // the tightened Sra-style eligibility: only tailwind inputs qualify
    private static readonly WatchConfig.ClassifyConfig Config = new()
    {
        Exclude = ["**/bin/**", "**/*.md", "Sra/wwwroot/css/app.css"],
        CssOnly = ["Sra/tailwind.input.css", "Sra/tailwind.config.js"],
        CssFastPath = true,
    };

    private static readonly Trigger Stop = new(TriggerKind.ClaudeStop);

    [Fact]
    public void Tailwind_input_only_is_css_only() =>
        Assert.Equal(PlanKind.CssOnly, Classifier.Plan(["Sra/tailwind.input.css"], Stop, Config).Kind);

    [Fact]
    public void Tailwind_config_only_is_css_only() =>
        Assert.Equal(PlanKind.CssOnly, Classifier.Plan(["Sra/tailwind.config.js"], Stop, Config).Kind);

    [Fact]
    public void Wwwroot_image_is_full() =>
        Assert.Equal(PlanKind.Full, Classifier.Plan(["Sra/wwwroot/img/logo.png"], Stop, Config).Kind);

    [Fact]
    public void Tailwind_plus_razor_is_full() =>
        Assert.Equal(PlanKind.Full,
            Classifier.Plan(["Sra/tailwind.input.css", "Sra/Components/Pages/Home.razor"], Stop, Config).Kind);
}

public class SseEventTests
{
    [Fact]
    public void Reload_event_format()
    {
        Assert.Equal("event: reload\ndata: {}\n\n", TriggerServer.FormatSse(SseEvent.Reload()));
    }

    [Fact]
    public void Css_update_event_carries_path_and_url()
    {
        var formatted = TriggerServer.FormatSse(SseEvent.CssUpdate("css/app.css", "http://127.0.0.1:43617/asset/css/app.css"));
        Assert.StartsWith("event: update-css\ndata: ", formatted);
        Assert.Contains("\"path\":\"css/app.css\"", formatted);
        Assert.Contains("\"url\":\"http://127.0.0.1:43617/asset/css/app.css\"", formatted);
        Assert.EndsWith("\n\n", formatted);
    }
}
