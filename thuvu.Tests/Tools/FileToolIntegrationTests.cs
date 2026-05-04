using System.Text.Json;
using thuvu.Tools;
using thuvu.Models;

namespace thuvu.Tests.Tools;

public class FileToolIntegrationTests
{
    private string CreateTestDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "thuvu-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void ReadFile_ExistingFile_ReturnsContentAndSha256()
    {
        var dir = CreateTestDir();
        try
        {
            var filePath = Path.Combine(dir, "readme.txt");
            File.WriteAllText(filePath, "hello integration test");

            var args = JsonSerializer.Serialize(new { path = filePath });
            var result = ReadFileToolImpl.ReadFileTool(args);

            var doc = JsonDocument.Parse(result);
            Assert.True(doc.RootElement.TryGetProperty("content", out var content));
            Assert.True(doc.RootElement.TryGetProperty("sha256", out var sha256));
            Assert.Contains("hello integration test", content.GetString());
            Assert.NotNull(sha256.GetString());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ReadFile_LineRange_ReturnsCorrectLines()
    {
        var dir = CreateTestDir();
        try
        {
            var filePath = Path.Combine(dir, "multiline.txt");
            File.WriteAllLines(filePath, new[] { "line1", "line2", "line3", "line4", "line5" });
            var args = JsonSerializer.Serialize(new { path = filePath, start_line = 2, end_line = 4, line_numbers = true });
            var result = ReadFileToolImpl.ReadFileTool(args);
            var doc = JsonDocument.Parse(result);
            Assert.True(doc.RootElement.TryGetProperty("content", out var content));
            var text = content.GetString()!;
            Assert.DoesNotContain("line1", text);
            Assert.Contains("line2", text);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ReadFile_NonExistentFile_ReturnsError()
    {
        var args = JsonSerializer.Serialize(new { path = "/nonexistent/file.txt" });
        var result = ReadFileToolImpl.ReadFileTool(args);
        var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void WriteFile_CreatesFileWithContent()
    {
        var dir = CreateTestDir();
        try
        {
            var filePath = Path.Combine(dir, "output.txt");
            var args = JsonSerializer.Serialize(new { path = filePath, content = "written content" });
            var result = WriteFileToolImpl.WriteFileTool(args);
            var doc = JsonDocument.Parse(result);
            Assert.True(doc.RootElement.TryGetProperty("sha256", out _));
            Assert.True(File.Exists(filePath));
            Assert.Equal("written content", File.ReadAllText(filePath));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void WriteFile_ChecksumMismatch_ReturnsError()
    {
        var dir = CreateTestDir();
        try
        {
            var filePath = Path.Combine(dir, "protected.txt");
            File.WriteAllText(filePath, "original content");
            var args = JsonSerializer.Serialize(new { path = filePath, content = "new content", expected_sha256 = "0000000000000000000000000000000000000000000000000000000000000000" });
            var result = WriteFileToolImpl.WriteFileTool(args);
            var doc = JsonDocument.Parse(result);
            Assert.True(doc.RootElement.TryGetProperty("error", out _));
            Assert.Equal("original content", File.ReadAllText(filePath));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void WriteFile_CreateIntermediateDirs_CreatesParents()
    {
        var dir = CreateTestDir();
        try
        {
            var filePath = Path.Combine(dir, "nested", "deep", "file.txt");
            var args = JsonSerializer.Serialize(new { path = filePath, content = "deep", create_intermediate_dirs = true });
            var result = WriteFileToolImpl.WriteFileTool(args);
            var doc = JsonDocument.Parse(result);
            Assert.True(doc.RootElement.TryGetProperty("sha256", out _));
            Assert.True(File.Exists(filePath));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void SearchFiles_ReturnsResults()
    {
        var dir = CreateTestDir();
        var prevWorkDir = AgentConfig.Config.WorkDirectory;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, ".git"));
            AgentConfig.Config.WorkDirectory = dir;
            File.WriteAllText(Path.Combine(dir, "test_search_1.cs"), "class Test1 {}");
            File.WriteAllText(Path.Combine(dir, "test_search_2.cs"), "class Test2 {}");
            var args = JsonSerializer.Serialize(new { glob = "test_search_*.cs" });
            var results = SearchFilesToolImpl.SearchFilesTool(args);
            Assert.NotEmpty(results);
        }
        finally
        {
            AgentConfig.Config.WorkDirectory = prevWorkDir;
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void SearchFiles_WithContentQuery_FiltersByContent()
    {
        var dir = CreateTestDir();
        var prevWorkDir = AgentConfig.Config.WorkDirectory;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, ".git"));
            AgentConfig.Config.WorkDirectory = dir;
            File.WriteAllText(Path.Combine(dir, "a.cs"), "class ClassA {}");
            File.WriteAllText(Path.Combine(dir, "b.cs"), "class ClassB {}");
            var args = JsonSerializer.Serialize(new { glob = "*.cs", query = "ClassA" });
            var results = SearchFilesToolImpl.SearchFilesTool(args);
            Assert.NotEmpty(results);
            Assert.Contains(results, r => r.Contains("a.cs"));
        }
        finally
        {
            AgentConfig.Config.WorkDirectory = prevWorkDir;
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ApplyPatch_AppliesUnifiedDiff()
    {
        var dir = CreateTestDir();
        try
        {
            var filePath = Path.Combine(dir, "patchtest.txt");
            File.WriteAllText(filePath, "line1\nline2\nline3\n");
            var patch = "--- a/patchtest.txt\n+++ b/patchtest.txt\n@@ -1,3 +1,3 @@\n line1\n-line2\n+modified line2\n line3\n";
            var args = JsonSerializer.Serialize(new { patch, root = dir });
            var result = ApplyPatchToolImpl.ApplyPatchTool(args);
            Assert.Contains("applied", result);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ApplyPatch_InvalidPatch_ReturnsError()
    {
        var args = JsonSerializer.Serialize(new { patch = "not a valid patch", root = "/tmp" });
        var result = ApplyPatchToolImpl.ApplyPatchTool(args);
        var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }
}
