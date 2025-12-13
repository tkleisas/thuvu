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
            try
            {
                // Check permissions before executing
                if (!PermissionManager.CheckPermission(name, argsJson))
                {
                    return JsonSerializer.Serialize(new { error = "Permission denied by user" });
                }

                switch (name)
                {
                    // Navigation / IO
                    case "search_files":
                        return JsonSerializer.Serialize(new { matches = SearchFilesToolImpl.SearchFilesTool(argsJson) });

                    case "read_file":
                        return ReadFileToolImpl.ReadFileTool(argsJson);

                    case "write_file":
                        return WriteFileToolImpl.WriteFileTool(argsJson);

                    case "apply_patch":
                        return ApplyPatchToolImpl.ApplyPatchTool(argsJson);

                    // Process runner
                    case "run_process":
                        return await RunProcessToolImpl.RunProcessToolAsync(argsJson);

                    // dotnet
                    case "dotnet_restore":
                        return await DotnetToolImpl.DotnetRestoreTool(argsJson);

                    case "dotnet_build":
                        return await DotnetToolImpl.DotnetBuildTool(argsJson);

                    case "dotnet_test":
                        return await DotnetToolImpl.DotnetTestTool(argsJson);

                    case "dotnet_run":
                        return await DotnetToolImpl.DotnetRunTool(argsJson);

                    case "dotnet_new":
                        return await DotnetToolImpl.DotnetNewTool(argsJson);

                    // git
                    case "git_status":
                        return await RunProcessToolImpl.RunProcessToolAsync(JsonSerializer.Serialize(Helpers.BuildGitStatusArgs(argsJson)));

                    case "git_diff":
                        return await RunProcessToolImpl.RunProcessToolAsync(JsonSerializer.Serialize(Helpers.BuildGitDiffArgs(argsJson)));

                    // NuGet
                    case "nuget_search":
                        return await RunProcessToolImpl.RunProcessToolAsync(JsonSerializer.Serialize(new
                        {
                            cmd = "dotnet",
                            args = new[] { "nuget", "search", Helpers.ExtractQuery(argsJson) }
                        }));

                    case "nuget_add":
                        return await RunProcessToolImpl.RunProcessToolAsync(JsonSerializer.Serialize(Helpers.BuildNugetAddArgs(argsJson)));

                    // RAG tools
                    case "rag_index":
                        return await RagToolImpl.RagIndexTool(argsJson, ct);

                    case "rag_search":
                        return await RagToolImpl.RagSearchTool(argsJson, ct);

                    case "rag_clear":
                        return await RagToolImpl.RagClearTool(argsJson, ct);

                    case "rag_stats":
                        return await RagToolImpl.RagStatsTool(argsJson, ct);

                    default:
                        return JsonSerializer.Serialize(new { error = $"Unknown tool: {name}" });
                }
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { error = ex.Message });
            }
        }
    }
}
