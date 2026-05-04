using System.Text.Json;
using thuvu.Tools;

namespace thuvu.Tests.Tools;

public class ToolImplsTests
{
    [Fact]
    public void Sha256_EmptyString_ReturnsExpectedHash()
    {
        var hash = ReadFileToolImpl.Sha256("");
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", hash);
    }

    [Fact]
    public void Sha256_KnownString_ReturnsExpectedHash()
    {
        var hash = ReadFileToolImpl.Sha256("hello world");
        Assert.Equal("b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde9", hash);
    }

    [Fact]
    public void Sha256_Deterministic_SameInputSameOutput()
    {
        var hash1 = ReadFileToolImpl.Sha256("test data");
        var hash2 = ReadFileToolImpl.Sha256("test data");
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void Sha256_DifferentInputs_DifferentOutputs()
    {
        var hash1 = ReadFileToolImpl.Sha256("test data");
        var hash2 = ReadFileToolImpl.Sha256("test data2");
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void SearchFilesTool_InvalidJson_Throws()
    {
        Assert.ThrowsAny<Exception>(() => SearchFilesToolImpl.SearchFilesTool("invalid json"));
    }

    [Fact]
    public void WriteFileTool_InvalidJson_ReturnsError()
    {
        var result = WriteFileToolImpl.WriteFileTool("not json");
        var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void WriteFileTool_MissingPath_ReturnsError()
    {
        var args = JsonSerializer.Serialize(new { content = "some content" });
        var result = WriteFileToolImpl.WriteFileTool(args);
        var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void WriteFileTool_MissingContent_ReturnsError()
    {
        var args = JsonSerializer.Serialize(new { path = "/tmp/test.cs" });
        var result = WriteFileToolImpl.WriteFileTool(args);
        var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void ApplyPatchTool_InvalidJson_ReturnsError()
    {
        var result = ApplyPatchToolImpl.ApplyPatchTool("}");
        var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void ApplyPatchTool_MissingPatch_ReturnsError()
    {
        var result = ApplyPatchToolImpl.ApplyPatchTool("{}");
        var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }
}
