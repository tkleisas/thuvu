using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace thuvu.Models
{
    /// <summary>
    /// Tool categories for organizing and deferring tool loading.
    /// </summary>
    public enum ToolCategory
    {
        /// <summary>Core tools always loaded (file operations)</summary>
        Core,
        /// <summary>Git operations</summary>
        Git,
        /// <summary>.NET development tools</summary>
        Dotnet,
        /// <summary>NuGet package management</summary>
        NuGet,
        /// <summary>RAG/semantic search tools</summary>
        Rag,
        /// <summary>Browser automation tools</summary>
        Browser,
        /// <summary>UI automation and screen capture</summary>
        UIAutomation,
        /// <summary>Process management tools</summary>
        Process,
        /// <summary>Code indexing and context tools</summary>
        CodeIndex,
        /// <summary>Multi-agent orchestration tools</summary>
        Agents,
        /// <summary>MCP code execution</summary>
        Mcp
    }

    public sealed class Tool
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "function";
        [JsonPropertyName("function")] public FunctionDef Function { get; set; } = default!;
        
        /// <summary>
        /// Category for tool organization and deferred loading.
        /// </summary>
        [JsonIgnore]
        public ToolCategory Category { get; set; } = ToolCategory.Core;
        
        /// <summary>
        /// If true, this tool is not loaded initially but can be discovered via tool_search.
        /// </summary>
        [JsonIgnore]
        public bool DeferLoading { get; set; } = false;
        
        /// <summary>
        /// Keywords for tool search matching (in addition to name and description).
        /// </summary>
        [JsonIgnore]
        public string[] SearchKeywords { get; set; } = Array.Empty<string>();
    }
}
