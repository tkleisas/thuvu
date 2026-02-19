using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace thuvu.Models
{
    /// <summary>
    /// Manages tool registration, categorization, and deferred loading.
    /// Implements the Tool Search pattern to reduce initial context size.
    /// </summary>
    public class ToolRegistry
    {
        private static ToolRegistry? _instance;
        public static ToolRegistry Instance => _instance ??= new ToolRegistry();

        private readonly Dictionary<string, Tool> _allTools = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<ToolCategory, List<Tool>> _toolsByCategory = new();
        private readonly HashSet<ToolCategory> _loadedCategories = new();
        private readonly HashSet<string> _loadedTools = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Categories that are always loaded (not deferred).
        /// </summary>
        public HashSet<ToolCategory> CoreCategories { get; } = new()
        {
            ToolCategory.Core
        };

        /// <summary>
        /// Register all tools from BuildTools.
        /// </summary>
        public void RegisterTools(IEnumerable<Tool> tools)
        {
            foreach (var tool in tools)
            {
                var name = tool.Function.Name;
                _allTools[name] = tool;

                if (!_toolsByCategory.TryGetValue(tool.Category, out var categoryList))
                {
                    categoryList = new List<Tool>();
                    _toolsByCategory[tool.Category] = categoryList;
                }
                categoryList.Add(tool);
            }
        }

        /// <summary>
        /// Get tools that should be loaded initially (core + any explicitly loaded).
        /// </summary>
        public List<Tool> GetInitialTools()
        {
            var tools = new List<Tool>();

            // Always include core tools
            foreach (var category in CoreCategories)
            {
                if (_toolsByCategory.TryGetValue(category, out var categoryTools))
                {
                    tools.AddRange(categoryTools);
                    _loadedCategories.Add(category);
                    foreach (var t in categoryTools)
                        _loadedTools.Add(t.Function.Name);
                }
            }

            // Include any non-deferred tools from other categories
            foreach (var tool in _allTools.Values)
            {
                if (!tool.DeferLoading && !_loadedTools.Contains(tool.Function.Name))
                {
                    tools.Add(tool);
                    _loadedTools.Add(tool.Function.Name);
                }
            }

            // Add the tool_search and tool_load tools
            var searchTool = CreateToolSearchTool();
            var loadTool = CreateToolLoadTool();
            tools.Add(searchTool);
            tools.Add(loadTool);
            _loadedTools.Add(searchTool.Function.Name);
            _loadedTools.Add(loadTool.Function.Name);

            return tools;
        }

        /// <summary>
        /// Get all tools (for backward compatibility when deferred loading is disabled).
        /// </summary>
        public List<Tool> GetAllTools()
        {
            return _allTools.Values.ToList();
        }

        /// <summary>
        /// Search for tools by query string.
        /// </summary>
        public List<ToolSearchResult> SearchTools(string query, int maxResults = 10)
        {
            if (string.IsNullOrWhiteSpace(query))
                return new List<ToolSearchResult>();

            var queryLower = query.ToLowerInvariant();
            var queryTerms = queryLower.Split(new[] { ' ', ',', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);

            var results = new List<(Tool tool, int score)>();

            foreach (var tool in _allTools.Values)
            {
                var score = CalculateMatchScore(tool, queryLower, queryTerms);
                if (score > 0)
                {
                    results.Add((tool, score));
                }
            }

            return results
                .OrderByDescending(r => r.score)
                .Take(maxResults)
                .Select(r => new ToolSearchResult
                {
                    Name = r.tool.Function.Name,
                    Description = r.tool.Function.Description ?? "",
                    Category = r.tool.Category.ToString(),
                    Score = r.score,
                    IsLoaded = _loadedTools.Contains(r.tool.Function.Name)
                })
                .ToList();
        }

        /// <summary>
        /// Load tools by name and return their full definitions.
        /// </summary>
        public List<Tool> LoadTools(IEnumerable<string> toolNames)
        {
            var loaded = new List<Tool>();

            foreach (var name in toolNames)
            {
                if (_allTools.TryGetValue(name, out var tool))
                {
                    if (!_loadedTools.Contains(name))
                    {
                        _loadedTools.Add(name);
                    }
                    loaded.Add(tool);
                }
            }

            return loaded;
        }

        /// <summary>
        /// Load all tools in a category.
        /// </summary>
        public List<Tool> LoadCategory(ToolCategory category)
        {
            if (!_toolsByCategory.TryGetValue(category, out var tools))
                return new List<Tool>();

            _loadedCategories.Add(category);
            foreach (var tool in tools)
                _loadedTools.Add(tool.Function.Name);

            return tools;
        }

        /// <summary>
        /// Get currently loaded tool names.
        /// </summary>
        public IReadOnlySet<string> GetLoadedToolNames() => _loadedTools;

        /// <summary>
        /// Check if a tool is currently loaded.
        /// </summary>
        public bool IsToolLoaded(string toolName) => _loadedTools.Contains(toolName);

        /// <summary>
        /// Get available categories with tool counts.
        /// </summary>
        public Dictionary<string, int> GetCategorySummary()
        {
            return _toolsByCategory.ToDictionary(
                kvp => kvp.Key.ToString(),
                kvp => kvp.Value.Count
            );
        }

        /// <summary>
        /// Get tools in a specific category.
        /// </summary>
        public List<Tool> GetToolsInCategory(ToolCategory category)
        {
            return _toolsByCategory.TryGetValue(category, out var tools) 
                ? tools 
                : new List<Tool>();
        }

        /// <summary>
        /// Reset loaded state (for new conversations).
        /// </summary>
        public void ResetLoadedState()
        {
            _loadedCategories.Clear();
            _loadedTools.Clear();
        }

        private int CalculateMatchScore(Tool tool, string queryLower, string[] queryTerms)
        {
            var score = 0;
            var name = tool.Function.Name.ToLowerInvariant();
            var description = (tool.Function.Description ?? "").ToLowerInvariant();
            var category = tool.Category.ToString().ToLowerInvariant();

            // Exact name match
            if (name == queryLower)
                score += 100;
            // Name contains query
            else if (name.Contains(queryLower))
                score += 50;

            // Category match
            if (category.Contains(queryLower))
                score += 30;

            // Description contains query
            if (description.Contains(queryLower))
                score += 20;

            // Check individual terms
            foreach (var term in queryTerms)
            {
                if (name.Contains(term))
                    score += 15;
                if (description.Contains(term))
                    score += 5;
                
                // Check search keywords
                foreach (var keyword in tool.SearchKeywords)
                {
                    if (keyword.ToLowerInvariant().Contains(term))
                        score += 10;
                }
            }

            return score;
        }

        private Tool CreateToolSearchTool()
        {
            return new Tool
            {
                Type = "function",
                Category = ToolCategory.Core,
                DeferLoading = false,
                Function = new FunctionDef
                {
                    Name = "tool_search",
                    Description = "Search for available tools by keyword or category. Use this to discover tools for specific tasks. Returns tool names and descriptions. After finding relevant tools, use tool_load to load their full definitions.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "query": {
                          "type": "string",
                          "description": "Search query - keywords like 'git', 'file', 'browser', 'test', 'rag', 'ui', 'process'"
                        },
                        "category": {
                          "type": "string",
                          "enum": ["Core", "Git", "Dotnet", "NuGet", "Rag", "Browser", "UIAutomation", "Process", "CodeIndex", "Agents", "Mcp"],
                          "description": "Optional: filter by category"
                        },
                        "max_results": {
                          "type": "integer",
                          "default": 10,
                          "description": "Maximum results to return"
                        }
                      },
                      "required": ["query"]
                    }
                    """).RootElement
                }
            };
        }
        
        private Tool CreateToolLoadTool()
        {
            return new Tool
            {
                Type = "function",
                Category = ToolCategory.Core,
                DeferLoading = false,
                Function = new FunctionDef
                {
                    Name = "tool_load",
                    Description = "Load tools to make them available for use. After using tool_search to find relevant tools, use this to load them. Loaded tools will be available in subsequent calls.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type": "object",
                      "properties": {
                        "tools": {
                          "type": "array",
                          "items": {"type": "string"},
                          "description": "List of tool names to load"
                        },
                        "category": {
                          "type": "string",
                          "enum": ["Core", "Git", "Dotnet", "NuGet", "Rag", "Browser", "UIAutomation", "Process", "CodeIndex", "Agents", "Mcp"],
                          "description": "Load all tools in this category"
                        }
                      }
                    }
                    """).RootElement
                }
            };
        }
    }

    public class ToolSearchResult
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Category { get; set; } = "";
        public int Score { get; set; }
        public bool IsLoaded { get; set; }
    }
}
