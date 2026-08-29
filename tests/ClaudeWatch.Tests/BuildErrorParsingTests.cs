using ClaudeWatch;
using Xunit;

namespace ClaudeWatch.Tests;

public class BuildErrorParsingTests
{
    [Fact]
    public void Parses_canonical_msbuild_error()
    {
        var errors = BuildRunner.ParseErrors(
            [@"C:\repo\Sra\Services\FooService.cs(42,13): error CS0103: The name 'bar' does not exist in the current context [C:\repo\Sra\Sra.csproj]"]);
        var e = Assert.Single(errors);
        Assert.Equal(@"C:\repo\Sra\Services\FooService.cs", e.File);
        Assert.Equal(42, e.Line);
        Assert.Equal("CS0103", e.Code);
        Assert.Equal("The name 'bar' does not exist in the current context", e.Message);
    }

    [Fact]
    public void Dedups_repeated_diagnostics_across_projects()
    {
        var line1 = @"C:\repo\A.cs(1,1): error CS0246: type not found [C:\repo\P1.csproj]";
        var line2 = @"C:\repo\A.cs(1,1): error CS0246: type not found [C:\repo\P2.csproj]";
        Assert.Single(BuildRunner.ParseErrors([line1, line2]));
    }

    [Fact]
    public void Parses_bare_msbuild_error()
    {
        var errors = BuildRunner.ParseErrors(["MSBUILD : error MSB1009: Project file does not exist."]);
        var e = Assert.Single(errors);
        Assert.Equal("MSB1009", e.Code);
        Assert.Equal(0, e.Line);
    }

    [Fact]
    public void Ignores_warnings_and_noise()
    {
        var errors = BuildRunner.ParseErrors(
        [
            @"C:\repo\A.cs(5,1): warning CS0219: unused variable",
            "  Determining projects to restore...",
            "Build succeeded.",
        ]);
        Assert.Empty(errors);
    }

    [Fact]
    public void Parses_razor_compiler_error()
    {
        var errors = BuildRunner.ParseErrors(
            [@"C:\repo\Sra\Components\Pages\Home.razor(10,5): error RZ1034: Found a malformed 'div' tag helper. [C:\repo\Sra\Sra.csproj]"]);
        var e = Assert.Single(errors);
        Assert.Equal("RZ1034", e.Code);
        Assert.Equal(10, e.Line);
    }
}
