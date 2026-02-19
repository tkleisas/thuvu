using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using thuvu.Tools;

namespace thuvu.Models
{
    /// <summary>
    /// JSON-RPC request from TypeScript sandbox
    /// </summary>
    public class JsonRpcRequest
    {
        public string Jsonrpc { get; set; } = "2.0";
        public int Id { get; set; }
        public string Method { get; set; } = string.Empty;
        public JsonElement? Params { get; set; }
    }

    /// <summary>
    /// JSON-RPC response to TypeScript sandbox
    /// </summary>
    public class JsonRpcResponse
    {
        public string Jsonrpc { get; set; } = "2.0";
        public int Id { get; set; }
        public object? Result { get; set; }
        public JsonRpcError? Error { get; set; }
    }

    /// <summary>
    /// JSON-RPC error
    /// </summary>
    public class JsonRpcError
    {
        public int Code { get; set; }
        public string Message { get; set; } = string.Empty;
        public object? Data { get; set; }
    }

    /// <summary>
    /// Bridge for handling tool calls from TypeScript sandbox
    /// </summary>
    public class McpBridge
    {
        private readonly ConcurrentDictionary<string, Func<string, CancellationToken, Task<string>>> _toolHandlers = new();
        private readonly List<ToolCallLog> _toolCallLogs = new();
        private readonly object _logLock = new();

        public McpBridge()
        {
            RegisterDefaultTools();
        }

        /// <summary>
        /// Register all default tool handlers
        /// </summary>
        private void RegisterDefaultTools()
        {
            // Filesystem tools
            RegisterTool("read_file", (args, ct) => Task.FromResult(ReadFileToolImpl.ReadFileTool(args)));
            RegisterTool("write_file", (args, ct) => Task.FromResult(WriteFileToolImpl.WriteFileTool(args)));
            RegisterTool("search_files", (args, ct) => 
                Task.FromResult(JsonSerializer.Serialize(new { matches = SearchFilesToolImpl.SearchFilesTool(args) })));
            RegisterTool("apply_patch", (args, ct) => Task.FromResult(ApplyPatchToolImpl.ApplyPatchTool(args)));

            // Process tools
            RegisterTool("run_process", (args, ct) => RunProcessToolImpl.RunProcessToolAsync(args));

            // Dotnet tools
            RegisterTool("dotnet_restore", (args, ct) => DotnetToolImpl.DotnetRestoreTool(args));
            RegisterTool("dotnet_build", (args, ct) => DotnetToolImpl.DotnetBuildTool(args));
            RegisterTool("dotnet_test", (args, ct) => DotnetToolImpl.DotnetTestTool(args));
            RegisterTool("dotnet_run", (args, ct) => DotnetToolImpl.DotnetRunTool(args));
            RegisterTool("dotnet_new", (args, ct) => DotnetToolImpl.DotnetNewTool(args));

            // Git tools
            RegisterTool("git_status", async (args, ct) => 
                await RunProcessToolImpl.RunProcessToolAsync(JsonSerializer.Serialize(Helpers.BuildGitStatusArgs(args))));
            RegisterTool("git_diff", async (args, ct) => 
                await RunProcessToolImpl.RunProcessToolAsync(JsonSerializer.Serialize(Helpers.BuildGitDiffArgs(args))));

            // RAG tools
            RegisterTool("rag_index", (args, ct) => RagToolImpl.RagIndexTool(args, ct));
            RegisterTool("rag_search", (args, ct) => RagToolImpl.RagSearchTool(args, ct));
            RegisterTool("rag_clear", (args, ct) => RagToolImpl.RagClearTool(args, ct));
            RegisterTool("rag_stats", (args, ct) => RagToolImpl.RagStatsTool(args, ct));

            // Catalog tools for progressive discovery
            RegisterTool("catalog_list", (args, ct) => Task.FromResult(GetToolCatalogList()));
            RegisterTool("catalog_search", (args, ct) => Task.FromResult(SearchToolCatalog(args)));
            RegisterTool("catalog_schema", (args, ct) => Task.FromResult(GetToolCatalogSchema(args)));
        }

        /// <summary>
        /// Get list of all available tools
        /// </summary>
        private string GetToolCatalogList()
        {
            var tools = _toolHandlers.Keys.Select(k => new { name = k, available = true }).ToList();
            return JsonSerializer.Serialize(new { tools, count = tools.Count });
        }

        /// <summary>
        /// Search tool catalog by query
        /// </summary>
        private string SearchToolCatalog(string argsJson)
        {
            using var doc = JsonDocument.Parse(argsJson);
            var query = doc.RootElement.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
            
            var matches = _toolHandlers.Keys
                .Where(k => k.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(k => new { name = k })
                .ToList();

            return JsonSerializer.Serialize(new { matches, count = matches.Count, query });
        }

        /// <summary>
        /// Get schema for a specific tool
        /// </summary>
        private string GetToolCatalogSchema(string argsJson)
        {
            using var doc = JsonDocument.Parse(argsJson);
            var toolName = doc.RootElement.TryGetProperty("tool", out var t) ? t.GetString() ?? "" : "";

            if (!_toolHandlers.ContainsKey(toolName))
            {
                return JsonSerializer.Serialize(new { error = $"Tool not found: {toolName}" });
            }

            // Return basic schema info (could be extended with full JSON schema)
            return JsonSerializer.Serialize(new
            {
                name = toolName,
                available = true,
                description = GetToolDescription(toolName)
            });
        }

        /// <summary>
        /// Get description for a tool
        /// </summary>
        private static string GetToolDescription(string toolName) => toolName switch
        {
            "read_file" => "Read file contents and get SHA256 hash",
            "write_file" => "Write content to a file",
            "search_files" => "Search for files by glob pattern and content",
            "apply_patch" => "Apply a unified diff patch",
            "run_process" => "Run a whitelisted command",
            "dotnet_restore" => "Restore .NET dependencies",
            "dotnet_build" => "Build .NET project",
            "dotnet_test" => "Run .NET tests",
            "dotnet_run" => "Run .NET project",
            "dotnet_new" => "Create new .NET project",
            "git_status" => "Get git repository status",
            "git_diff" => "Get git diff",
            "rag_index" => "Index files for semantic search",
            "rag_search" => "Semantic search indexed content",
            "rag_clear" => "Clear RAG index",
            "rag_stats" => "Get RAG index statistics",
            "catalog_list" => "List all available tools",
            "catalog_search" => "Search for tools by name",
            "catalog_schema" => "Get tool schema",
            _ => "No description available"
        };

        /// <summary>
        /// Register a tool handler
        /// </summary>
        public void RegisterTool(string name, Func<string, CancellationToken, Task<string>> handler)
        {
            _toolHandlers[name] = handler;
        }

        /// <summary>
        /// Handle a JSON-RPC request from the sandbox
        /// </summary>
        public async Task<JsonRpcResponse> HandleRequestAsync(JsonRpcRequest request, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var toolName = request.Method;
            var argsJson = request.Params?.GetRawText() ?? "{}";

            try
            {
                // Validate paths for file operations
                var pathValidation = ValidatePaths(toolName, argsJson);
                if (!pathValidation.IsValid)
                {
                    return new JsonRpcResponse
                    {
                        Id = request.Id,
                        Error = new JsonRpcError
                        {
                            Code = -32600,
                            Message = $"Path validation failed: {pathValidation.Error}"
                        }
                    };
                }

                // Check permissions (use async version for Web UI support)
                if (!await PermissionManager.CheckPermissionAsync(toolName, argsJson))
                {
                    return new JsonRpcResponse
                    {
                        Id = request.Id,
                        Error = new JsonRpcError
                        {
                            Code = -32600,
                            Message = "Permission denied by user"
                        }
                    };
                }

                // Find handler
                if (!_toolHandlers.TryGetValue(toolName, out var handler))
                {
                    return new JsonRpcResponse
                    {
                        Id = request.Id,
                        Error = new JsonRpcError
                        {
                            Code = -32601,
                            Message = $"Unknown tool: {toolName}"
                        }
                    };
                }

                // Execute tool
                var result = await handler(argsJson, ct);
                sw.Stop();

                // Log the call
                LogToolCall(toolName, argsJson, result, sw.Elapsed, true);

                // Parse result as JSON for proper response
                using var doc = JsonDocument.Parse(result);
                return new JsonRpcResponse
                {
                    Id = request.Id,
                    Result = doc.RootElement.Clone()
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                LogToolCall(toolName, argsJson, ex.Message, sw.Elapsed, false);

                return new JsonRpcResponse
                {
                    Id = request.Id,
                    Error = new JsonRpcError
                    {
                        Code = -32603,
                        Message = ex.Message
                    }
                };
            }
        }

        /// <summary>
        /// Log a tool call for auditing
        /// </summary>
        private void LogToolCall(string toolName, string args, string result, TimeSpan duration, bool success)
        {
            lock (_logLock)
            {
                _toolCallLogs.Add(new ToolCallLog
                {
                    ToolName = toolName,
                    Arguments = args,
                    Result = result,
                    Duration = duration,
                    Success = success
                });
            }

            if (McpConfig.Instance.AuditLog)
            {
                AgentLogger.LogInfo("MCP Tool Call: {Tool}({Args}) => {Success} in {Duration}ms",
                    toolName, args.Length > 100 ? args[..100] + "..." : args, success, duration.TotalMilliseconds);
            }
        }

        /// <summary>
        /// Get all tool call logs and clear the list
        /// </summary>
        public List<ToolCallLog> GetAndClearLogs()
        {
            lock (_logLock)
            {
                var logs = new List<ToolCallLog>(_toolCallLogs);
                _toolCallLogs.Clear();
                return logs;
            }
        }

        /// <summary>
        /// Get list of available tools
        /// </summary>
        public IEnumerable<string> GetAvailableTools() => _toolHandlers.Keys;

        /// <summary>
        /// Validate paths in tool arguments to ensure they're within project directory
        /// </summary>
        private static (bool IsValid, string? Error) ValidatePaths(string toolName, string argsJson)
        {
            // Tools that involve file paths
            var pathTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "read_file", "write_file", "search_files", "apply_patch",
                "rag_index", "dotnet_build", "dotnet_test", "dotnet_run", "dotnet_new"
            };

            if (!pathTools.Contains(toolName))
            {
                return (true, null);
            }

            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                var root = doc.RootElement;

                // Check 'path' property
                if (root.TryGetProperty("path", out var pathEl) && pathEl.ValueKind == JsonValueKind.String)
                {
                    var path = pathEl.GetString();
                    if (!IsPathSafe(path))
                    {
                        return (false, $"Path '{path}' is outside the project directory");
                    }
                }

                // Check 'solution_or_project' property
                if (root.TryGetProperty("solution_or_project", out var projEl) && projEl.ValueKind == JsonValueKind.String)
                {
                    var path = projEl.GetString();
                    if (!IsPathSafe(path))
                    {
                        return (false, $"Path '{path}' is outside the project directory");
                    }
                }

                return (true, null);
            }
            catch
            {
                return (true, null); // If we can't parse, let it through (will fail later with better error)
            }
        }

        /// <summary>
        /// Check if a path is within the work directory
        /// </summary>
        private static bool IsPathSafe(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return true; // Empty paths are fine

            try
            {
                var workDir = AgentConfig.GetWorkDirectory();
                var fullPath = Path.GetFullPath(path, workDir);
                
                // Normalize paths for comparison
                var normalizedRoot = Path.GetFullPath(workDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var normalizedPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                // Check if path starts with work directory
                return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false; // Invalid paths are not safe
            }
        }

        /// <summary>
        /// Dangerous path patterns that should be blocked
        /// </summary>
        private static readonly string[] DangerousPaths = new[]
        {
            "..",           // Parent directory traversal
            "~",            // Home directory
            "/etc",         // System config
            "/usr",         // System binaries
            "C:\\Windows",  // Windows system
            "C:\\Program"   // Program files
        };
    }
}
