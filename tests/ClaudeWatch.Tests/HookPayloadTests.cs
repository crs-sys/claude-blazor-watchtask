using System.Text;
using ClaudeWatch;
using Xunit;

namespace ClaudeWatch.Tests;

public class HookPayloadTests
{
    private static Stream Body(string json) => new MemoryStream(Encoding.UTF8.GetBytes(json));

    [Fact]
    public async Task Extracts_edit_tool_file_path()
    {
        var paths = await TriggerServer.ExtractFilePathsAsync(Body("""
            {"hook_event_name":"PostToolUse","tool_name":"Edit",
             "tool_input":{"file_path":"C:\\repo\\Sra\\Foo.cs","old_string":"a","new_string":"b"}}
            """));
        Assert.Equal([@"C:\repo\Sra\Foo.cs"], paths);
    }

    [Fact]
    public async Task Extracts_notebook_path()
    {
        var paths = await TriggerServer.ExtractFilePathsAsync(Body("""
            {"tool_name":"NotebookEdit","tool_input":{"notebook_path":"C:\\repo\\nb.ipynb"}}
            """));
        Assert.Equal([@"C:\repo\nb.ipynb"], paths);
    }

    [Fact]
    public async Task Extracts_multiedit_edits_array_paths()
    {
        var paths = await TriggerServer.ExtractFilePathsAsync(Body("""
            {"tool_name":"MultiEdit","tool_input":{"file_path":"C:\\repo\\A.cs",
             "edits":[{"file_path":"C:\\repo\\B.cs"},{"old_string":"x","new_string":"y"}]}}
            """));
        Assert.Contains(@"C:\repo\A.cs", paths);
        Assert.Contains(@"C:\repo\B.cs", paths);
    }

    [Fact]
    public async Task Accepts_simple_changed_form()
    {
        var paths = await TriggerServer.ExtractFilePathsAsync(Body("""{"file_path":"Sra/Foo.cs"}"""));
        Assert.Equal(["Sra/Foo.cs"], paths);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"tool_name":"Bash","tool_input":{"command":"ls"}}""")]
    public async Task Malformed_or_irrelevant_payloads_yield_nothing(string json)
    {
        var paths = await TriggerServer.ExtractFilePathsAsync(Body(json));
        Assert.Empty(paths);
    }

    [Fact]
    public async Task Extracts_session_id_from_stop_payload()
    {
        var sid = await TriggerServer.ExtractSessionIdAsync(Body("""{"hook_event_name":"Stop","session_id":"abc123"}"""));
        Assert.Equal("abc123", sid);
    }
}
