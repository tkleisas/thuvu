using System.Text.Json;
using thuvu.Tools;

namespace thuvu.Tests.Tools;

public class HelpersTests
{
    [Fact]
    public void BuildGitStatusArgs_NoRoot_ReturnsDefaultArgs()
    {
        var argsJson = "{}";
        var result = Helpers.BuildGitStatusArgs(argsJson);

        var json = JsonSerializer.Serialize(result);
        Assert.Contains("git", json);
        Assert.Contains("status", json);
    }

    [Fact]
    public void BuildGitStatusArgs_WithRoot_IncludesCwd()
    {
        var argsJson = "{\"root\":\"/custom/path\"}";
        var result = Helpers.BuildGitStatusArgs(argsJson);

        var json = JsonSerializer.Serialize(result);
        Assert.Contains("/custom/path", json);
    }

    [Fact]
    public void BuildGitDiffArgs_BasicDiff_ReturnsDefaultArgs()
    {
        var argsJson = "{}";
        var result = Helpers.BuildGitDiffArgs(argsJson);

        Assert.NotNull(result);
    }

    [Fact]
    public void BuildGitDiffArgs_Staged_IncludesStagedFlag()
    {
        var argsJson = "{\"staged\":true}";
        var result = Helpers.BuildGitDiffArgs(argsJson);

        var json = JsonSerializer.Serialize(result);
        Assert.Contains("staged", json);
    }

    [Fact]
    public void BuildGitDiffArgs_WithContext_IncludesUFlag()
    {
        var argsJson = "{\"context\":5}";
        var result = Helpers.BuildGitDiffArgs(argsJson);

        var json = JsonSerializer.Serialize(result);
        Assert.Contains("-U", json);
        Assert.Contains("5", json);
    }

    [Fact]
    public void BuildGitDiffArgs_ContextClamped_ToZero()
    {
        var argsJson = "{\"context\":-1}";
        var result = Helpers.BuildGitDiffArgs(argsJson);

        var json = JsonSerializer.Serialize(result);
        Assert.Contains("0", json);
    }

    [Fact]
    public void BuildGitDiffArgs_ContextClamped_ToHundred()
    {
        var argsJson = "{\"context\":150}";
        var result = Helpers.BuildGitDiffArgs(argsJson);

        var json = JsonSerializer.Serialize(result);
        Assert.Contains("100", json);
    }

    [Fact]
    public void BuildGitDiffArgs_WithPaths_IncludesPathArgs()
    {
        var argsJson = "{\"paths\":[\"file1.cs\",\"file2.cs\"]}";
        var result = Helpers.BuildGitDiffArgs(argsJson);

        var json = JsonSerializer.Serialize(result);
        Assert.Contains("file1.cs", json);
        Assert.Contains("file2.cs", json);
    }

    [Fact]
    public void BuildNugetAddArgs_WithRequiredId_ReturnsCorrectArgs()
    {
        var argsJson = "{\"id\":\"Newtonsoft.Json\"}";
        var result = Helpers.BuildNugetAddArgs(argsJson);

        var json = JsonSerializer.Serialize(result);
        Assert.Contains("Newtonsoft.Json", json);
        Assert.Contains("add", json);
        Assert.Contains("package", json);
    }

    [Fact]
    public void BuildNugetAddArgs_WithVersion_IncludesVersionArg()
    {
        var argsJson = "{\"id\":\"Newtonsoft.Json\",\"version\":\"13.0.1\"}";
        var result = Helpers.BuildNugetAddArgs(argsJson);

        var json = JsonSerializer.Serialize(result);
        Assert.Contains("13.0.1", json);
    }

    [Fact]
    public void BuildNugetAddArgs_WithProject_IncludesProjectArg()
    {
        var argsJson = "{\"id\":\"Newtonsoft.Json\",\"project\":\"/path/to/proj.csproj\"}";
        var result = Helpers.BuildNugetAddArgs(argsJson);

        var json = JsonSerializer.Serialize(result);
        Assert.Contains("proj.csproj", json);
    }

    [Fact]
    public void BuildNugetAddArgs_IdRequired_ThrowsForMissingId()
    {
        var argsJson = "{}";
        Assert.ThrowsAny<Exception>(() => Helpers.BuildNugetAddArgs(argsJson));
    }

    [Fact]
    public void ExtractQuery_WithQuery_ReturnsQuery()
    {
        var argsJson = "{\"query\":\"Newtonsoft\"}";
        var result = Helpers.ExtractQuery(argsJson);
        Assert.Equal("Newtonsoft", result);
    }

    [Fact]
    public void ExtractQuery_EmptyJson_ReturnsEmpty()
    {
        var argsJson = "{}";
        var result = Helpers.ExtractQuery(argsJson);
        Assert.Equal("", result);
    }

    [Fact]
    public void GetCurrentGitTag_ReturnsNonNull()
    {
        var tag = Helpers.GetCurrentGitTag();
        Assert.NotNull(tag);
    }
}
