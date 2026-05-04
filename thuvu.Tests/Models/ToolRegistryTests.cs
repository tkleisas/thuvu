using thuvu.Models;

namespace thuvu.Tests.Models;

public class ToolRegistryTests
{
    private readonly List<Tool> _sampleTools;

    public ToolRegistryTests()
    {
        _sampleTools = new List<Tool>
        {
            new()
            {
                Type = "function",
                Category = ToolCategory.Core,
                DeferLoading = false,
                Function = new FunctionDef
                {
                    Name = "read_file",
                    Description = "Reads a file from the filesystem",
                    Parameters = System.Text.Json.JsonDocument.Parse("{}").RootElement
                }
            },
            new()
            {
                Type = "function",
                Category = ToolCategory.Browser,
                DeferLoading = true,
                SearchKeywords = new[] { "web", "url", "http" },
                Function = new FunctionDef
                {
                    Name = "browser_navigate",
                    Description = "Navigates to a URL using the browser",
                    Parameters = System.Text.Json.JsonDocument.Parse("{}").RootElement
                }
            },
            new()
            {
                Type = "function",
                Category = ToolCategory.Git,
                DeferLoading = true,
                Function = new FunctionDef
                {
                    Name = "git_status",
                    Description = "Shows git working tree status",
                    Parameters = System.Text.Json.JsonDocument.Parse("{}").RootElement
                }
            }
        };
    }

    [Fact]
    public void RegisterTools_PopulatesDictionary()
    {
        var registry = new ToolRegistry();
        registry.RegisterTools(_sampleTools);

        Assert.Equal(3, registry.GetAllTools().Count);
    }

    [Fact]
    public void GetInitialTools_ReturnsCoreAndNonDeferred()
    {
        var registry = new ToolRegistry();
        registry.RegisterTools(_sampleTools);

        var initial = registry.GetInitialTools();

        Assert.Contains(initial, t => t.Function.Name == "read_file");
        Assert.DoesNotContain(initial, t => t.Function.Name == "browser_navigate");
    }

    [Fact]
    public void SearchTools_ByName_ReturnsExactMatch()
    {
        var registry = new ToolRegistry();
        registry.RegisterTools(_sampleTools);

        var results = registry.SearchTools("read_file", 10);

        Assert.Single(results);
        Assert.Equal("read_file", results[0].Name);
        Assert.True(results[0].Score >= 90);
    }

    [Fact]
    public void SearchTools_ByDescription_ReturnsMatch()
    {
        var registry = new ToolRegistry();
        registry.RegisterTools(_sampleTools);

        var results = registry.SearchTools("browser", 10);

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.Name == "browser_navigate");
    }

    [Fact]
    public void SearchTools_ByKeyword_ReturnsMatch()
    {
        var registry = new ToolRegistry();
        registry.RegisterTools(_sampleTools);

        var results = registry.SearchTools("web", 10);

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.Name == "browser_navigate");
    }

    [Fact]
    public void SearchTools_NoMatches_ReturnsEmpty()
    {
        var registry = new ToolRegistry();
        registry.RegisterTools(_sampleTools);

        var results = registry.SearchTools("nonexistent_xyz", 10);

        Assert.Empty(results);
    }

    [Fact]
    public void SearchTools_RespectsMaxResults()
    {
        var manyTools = new List<Tool>();
        for (int i = 0; i < 20; i++)
        {
            manyTools.Add(new Tool
            {
                Function = new FunctionDef
                {
                    Name = $"tool_{i}",
                    Description = "a common description",
                    Parameters = System.Text.Json.JsonDocument.Parse("{}").RootElement
                }
            });
        }

        var registry = new ToolRegistry();
        registry.RegisterTools(manyTools);

        var results = registry.SearchTools("common", 5);
        Assert.True(results.Count <= 5);
    }

    [Fact]
    public void LoadTools_ByName_LoadsCorrectly()
    {
        var registry = new ToolRegistry();
        registry.RegisterTools(_sampleTools);

        var loaded = registry.LoadTools(new[] { "browser_navigate" });

        Assert.Single(loaded);
        Assert.True(registry.IsToolLoaded("browser_navigate"));
        Assert.False(registry.IsToolLoaded("git_status"));
    }

    [Fact]
    public void LoadTools_UnknownName_ReturnsEmpty()
    {
        var registry = new ToolRegistry();
        registry.RegisterTools(_sampleTools);

        var loaded = registry.LoadTools(new[] { "nonexistent_tool" });

        Assert.Empty(loaded);
    }

    [Fact]
    public void LoadCategory_LoadsAllCategoryTools()
    {
        var registry = new ToolRegistry();
        registry.RegisterTools(_sampleTools);

        var loaded = registry.LoadCategory(ToolCategory.Git);

        Assert.Single(loaded);
        Assert.Equal("git_status", loaded[0].Function.Name);
    }

    [Fact]
    public void GetLoadedToolNames_ReturnsLoadedSet()
    {
        var registry = new ToolRegistry();
        registry.RegisterTools(_sampleTools);
        registry.LoadTools(new[] { "read_file" });

        var names = registry.GetLoadedToolNames();

        Assert.Contains("read_file", names);
        Assert.DoesNotContain("browser_navigate", names);
    }

    [Fact]
    public void GetCategorySummary_ReturnsCorrectCounts()
    {
        var registry = new ToolRegistry();
        registry.RegisterTools(_sampleTools);

        var summary = registry.GetCategorySummary();

        Assert.True(summary.Count > 0);
    }

    [Fact]
    public void GetToolsInCategory_ReturnsCategoryTools()
    {
        var registry = new ToolRegistry();
        registry.RegisterTools(_sampleTools);

        var tools = registry.GetToolsInCategory(ToolCategory.Browser);

        Assert.Single(tools);
        Assert.Equal("browser_navigate", tools[0].Function.Name);
    }

    [Fact]
    public void ResetLoadedState_ClearsLoadedTools()
    {
        var registry = new ToolRegistry();
        registry.RegisterTools(_sampleTools);
        registry.LoadTools(new[] { "read_file" });
        Assert.True(registry.IsToolLoaded("read_file"));

        registry.ResetLoadedState();

        Assert.False(registry.IsToolLoaded("read_file"));
    }

    [Fact]
    public void Instance_ReturnsSameSingleton()
    {
        var instance1 = ToolRegistry.Instance;
        var instance2 = ToolRegistry.Instance;

        Assert.Same(instance1, instance2);
    }
}
