using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using thuvu.Models;
using thuvu.Tools;

namespace thuvu
{
    /// <summary>
    /// Executes tools by name with permission checking
    /// </summary>
    public static class ToolExecutor
    {
        /// <summary>
        /// Executes a tool by name, returning a JSON string result
        /// </summary>
        public static async Task<string> ExecuteToolAsync(string name, string argsJson, CancellationToken ct)
        {
            var startTime = DateTime.Now;
            SessionLogger.Instance.LogToolStart(name, argsJson);
            
            try
            {
                // Check permissions before executing
                if (!PermissionManager.CheckPermission(name, argsJson))
                {
                    var error = "Permission denied by user";
                    SessionLogger.Instance.LogToolError(name, error);
                    return JsonSerializer.Serialize(new { error });
                }

                string result;
                switch (name)
                {
                    // Navigation / IO
                    case "search_files":
                        result = JsonSerializer.Serialize(new { matches = SearchFilesToolImpl.SearchFilesTool(argsJson) });
                        break;

                    case "read_file":
                        result = ReadFileToolImpl.ReadFileTool(argsJson);
                        break;

                    case "write_file":
                        result = WriteFileToolImpl.WriteFileTool(argsJson);
                        break;

                    case "apply_patch":
                        result = ApplyPatchToolImpl.ApplyPatchTool(argsJson);
                        break;

                    // Process runner
                    case "run_process":
                        result = await RunProcessToolImpl.RunProcessToolAsync(argsJson);
                        break;

                    // dotnet
                    case "dotnet_restore":
                        result = await DotnetToolImpl.DotnetRestoreTool(argsJson);
                        break;

                    case "dotnet_build":
                        result = await DotnetToolImpl.DotnetBuildTool(argsJson);
                        break;

                    case "dotnet_test":
                        result = await DotnetToolImpl.DotnetTestTool(argsJson);
                        break;

                    case "dotnet_run":
                        result = await DotnetToolImpl.DotnetRunTool(argsJson);
                        break;

                    case "dotnet_new":
                        result = await DotnetToolImpl.DotnetNewTool(argsJson);
                        break;

                    // git
                    case "git_status":
                        result = await RunProcessToolImpl.RunProcessToolAsync(JsonSerializer.Serialize(Helpers.BuildGitStatusArgs(argsJson)));
                        break;

                    case "git_diff":
                        result = await RunProcessToolImpl.RunProcessToolAsync(JsonSerializer.Serialize(Helpers.BuildGitDiffArgs(argsJson)));
                        break;

                    // NuGet
                    case "nuget_search":
                        result = await RunProcessToolImpl.RunProcessToolAsync(JsonSerializer.Serialize(new
                        {
                            cmd = "dotnet",
                            args = new[] { "nuget", "search", Helpers.ExtractQuery(argsJson) }
                        }));
                        break;

                    case "nuget_add":
                        result = await RunProcessToolImpl.RunProcessToolAsync(JsonSerializer.Serialize(Helpers.BuildNugetAddArgs(argsJson)));
                        break;

                    // RAG tools
                    case "rag_index":
                        result = await RagToolImpl.RagIndexTool(argsJson, ct);
                        break;

                    case "rag_search":
                        result = await RagToolImpl.RagSearchTool(argsJson, ct);
                        break;

                    case "rag_clear":
                        result = await RagToolImpl.RagClearTool(argsJson, ct);
                        break;

                    case "rag_stats":
                        result = await RagToolImpl.RagStatsTool(argsJson, ct);
                        break;

                    default:
                        result = JsonSerializer.Serialize(new { error = $"Unknown tool: {name}" });
                        break;
                }
                
                var elapsedMs = (DateTime.Now - startTime).TotalMilliseconds;
                SessionLogger.Instance.LogToolEnd(name, result, elapsedMs);
                return result;
            }
            catch (Exception ex)
            {
                SessionLogger.Instance.LogToolError(name, ex.Message);
                return JsonSerializer.Serialize(new { error = ex.Message });
            }
        }
    }
}
