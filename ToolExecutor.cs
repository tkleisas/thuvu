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
        /// Default tool execution timeout (2 minutes)
        /// </summary>
        public static TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromMinutes(60);
        
        /// <summary>
        /// Executes a tool by name, returning a JSON string result
        /// </summary>
        public static Task<string> ExecuteToolAsync(string name, string argsJson, CancellationToken ct)
            => ExecuteToolAsync(name, argsJson, ct, null, null);
        
        /// <summary>
        /// Executes a tool by name with progress reporting
        /// </summary>
        public static async Task<string> ExecuteToolAsync(
            string name, 
            string argsJson, 
            CancellationToken ct,
            ToolProgressCallback? onProgress,
            TimeSpan? timeout = null)
        {
            var effectiveTimeout = timeout ?? DefaultTimeout;
            var progress = new ToolProgress
            {
                ToolName = name,
                ArgsJson = argsJson,
                Status = ToolStatus.Pending,
                StartTime = DateTime.Now,
                Timeout = effectiveTimeout
            };
            
            SessionLogger.Instance.LogToolStart(name, argsJson);
            SessionLogger.Instance.LogInfo($"ExecuteToolAsync on thread {Environment.CurrentManagedThreadId}");
            
            // Report initial progress
            progress.Status = ToolStatus.Running;
            progress.Message = "Starting...";
            onProgress?.Invoke(progress);
            SessionLogger.Instance.LogToolProgress(name, ToolStatus.Running, "0.0s", "Starting");
            SessionLogger.Instance.LogInfo($"After initial progress on thread {Environment.CurrentManagedThreadId}");
            
            // Start progress reporting task if callback provided
            CancellationTokenSource? progressCts = null;
            Task? progressTask = null;
            
            SessionLogger.Instance.LogInfo($"About to check onProgress for {name}, onProgress is {(onProgress == null ? "null" : "not null")}");
            
            if (onProgress != null)
            {
                SessionLogger.Instance.LogInfo($"Creating progress task for {name}");
                progressCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                progressTask = Task.Run(async () =>
                {
                    try
                    {
                        SessionLogger.Instance.LogInfo($"Progress task started for {name}");
                        while (!progressCts.Token.IsCancellationRequested)
                        {
                            await Task.Delay(500, progressCts.Token).ConfigureAwait(false);
                            progress.Message = $"Running... {progress.ElapsedFormatted}";
                            SessionLogger.Instance.LogToolProgress(name, progress.Status, progress.ElapsedFormatted, "tick");
                            onProgress(progress);
                        }
                    }
                    catch (OperationCanceledException) 
                    { 
                        SessionLogger.Instance.LogInfo($"Progress task cancelled for {name}");
                    }
                    catch (Exception ex)
                    {
                        SessionLogger.Instance.LogError($"Progress task error for {name}: {ex.Message}");
                    }
                }, progressCts.Token);
                SessionLogger.Instance.LogInfo($"Progress task queued for {name}");
            }
            else
            {
                SessionLogger.Instance.LogInfo($"onProgress is null for {name}, skipping progress task");
            }
            
            try
            {
                // Check permissions before executing (use async version for Web UI support)
                if (!await PermissionManager.CheckPermissionAsync(name, argsJson))
                {
                    var error = "Permission denied by user";
                    SessionLogger.Instance.LogToolError(name, error);
                    progress.Status = ToolStatus.Failed;
                    progress.Result = error;
                    onProgress?.Invoke(progress);
                    return JsonSerializer.Serialize(new { error });
                }

                // Create timeout cancellation
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(effectiveTimeout);
                
                string result;
                try
                {
                    result = await ExecuteToolCoreAsync(name, argsJson, timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    // Timeout occurred
                    var elapsedMs = (DateTime.Now - progress.StartTime).TotalMilliseconds;
                    SessionLogger.Instance.LogToolTimeout(name, elapsedMs, effectiveTimeout.TotalMilliseconds);
                    progress.Status = ToolStatus.TimedOut;
                    progress.TimedOut = true;
                    progress.Result = $"Timeout after {progress.ElapsedFormatted}";
                    onProgress?.Invoke(progress);
                    return JsonSerializer.Serialize(new { error = "timeout", timed_out = true, elapsed_ms = elapsedMs });
                }
                
                var elapsed = (DateTime.Now - progress.StartTime).TotalMilliseconds;
                SessionLogger.Instance.LogToolEnd(name, result, elapsed);
                
                // Determine success/failure from result
                bool isError = result.Contains("\"error\"") || result.Contains("\"timed_out\":true");
                progress.Status = isError ? ToolStatus.Failed : ToolStatus.Completed;
                progress.Result = result;
                onProgress?.Invoke(progress);
                SessionLogger.Instance.LogToolProgress(name, progress.Status, progress.ElapsedFormatted, 
                    isError ? "Failed" : "Completed");
                
                return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                progress.Status = ToolStatus.Cancelled;
                progress.Cancelled = true;
                onProgress?.Invoke(progress);
                SessionLogger.Instance.LogToolProgress(name, ToolStatus.Cancelled, progress.ElapsedFormatted, "User cancelled");
                throw;
            }
            catch (Exception ex)
            {
                SessionLogger.Instance.LogToolError(name, ex.Message);
                progress.Status = ToolStatus.Failed;
                progress.Result = ex.Message;
                onProgress?.Invoke(progress);
                return JsonSerializer.Serialize(new { error = ex.Message });
            }
            finally
            {
                // Stop progress reporting
                if (progressCts != null)
                {
                    progressCts.Cancel();
                    if (progressTask != null)
                    {
                        try { await progressTask.ConfigureAwait(false); } catch { }
                    }
                    progressCts.Dispose();
                }
            }
        }
        
        /// <summary>
        /// Core tool execution logic. Synchronous tools are wrapped in Task.Run 
        /// to allow progress updates to occur during execution.
        /// </summary>
        private static async Task<string> ExecuteToolCoreAsync(string name, string argsJson, CancellationToken ct)
        {
            return name switch
            {
                // Navigation / IO - wrapped in Task.Run to allow progress updates
                "search_files" => await Task.Run(() => 
                    JsonSerializer.Serialize(new { matches = SearchFilesToolImpl.SearchFilesTool(argsJson, ct) }), ct).ConfigureAwait(false),
                "read_file" => await Task.Run(() => ReadFileToolImpl.ReadFileTool(argsJson), ct).ConfigureAwait(false),
                "write_file" => await Task.Run(() => WriteFileToolImpl.WriteFileTool(argsJson), ct).ConfigureAwait(false),
                "apply_patch" => await Task.Run(() => ApplyPatchToolImpl.ApplyPatchTool(argsJson), ct).ConfigureAwait(false),
                
                // Process runner
                "run_process" => await RunProcessToolImpl.RunProcessToolAsync(argsJson, ct).ConfigureAwait(false),
                
                // dotnet
                "dotnet_restore" => await DotnetToolImpl.DotnetRestoreTool(argsJson).ConfigureAwait(false),
                "dotnet_build" => await DotnetToolImpl.DotnetBuildTool(argsJson).ConfigureAwait(false),
                "dotnet_test" => await DotnetToolImpl.DotnetTestTool(argsJson).ConfigureAwait(false),
                "dotnet_run" => await DotnetToolImpl.DotnetRunTool(argsJson).ConfigureAwait(false),
                "dotnet_new" => await DotnetToolImpl.DotnetNewTool(argsJson).ConfigureAwait(false),
                
                // git
                "git_status" => await RunProcessToolImpl.RunProcessToolAsync(JsonSerializer.Serialize(Helpers.BuildGitStatusArgs(argsJson)), ct).ConfigureAwait(false),
                "git_diff" => await RunProcessToolImpl.RunProcessToolAsync(JsonSerializer.Serialize(Helpers.BuildGitDiffArgs(argsJson)), ct).ConfigureAwait(false),
                
                // NuGet
                "nuget_search" => await RunProcessToolImpl.RunProcessToolAsync(JsonSerializer.Serialize(new
                {
                    cmd = "dotnet",
                    args = new[] { "nuget", "search", Helpers.ExtractQuery(argsJson) }
                }), ct).ConfigureAwait(false),
                "nuget_add" => await RunProcessToolImpl.RunProcessToolAsync(JsonSerializer.Serialize(Helpers.BuildNugetAddArgs(argsJson)), ct).ConfigureAwait(false),
                
                // RAG tools
                "rag_index" => await RagToolImpl.RagIndexTool(argsJson, ct).ConfigureAwait(false),
                "rag_search" => await RagToolImpl.RagSearchTool(argsJson, ct).ConfigureAwait(false),
                "rag_clear" => await RagToolImpl.RagClearTool(argsJson, ct).ConfigureAwait(false),
                "rag_stats" => await RagToolImpl.RagStatsTool(argsJson, ct).ConfigureAwait(false),
                
                // MCP code execution
                "execute_code" => await ExecuteCodeToolAsync(argsJson, ct).ConfigureAwait(false),
                
                // Browser tools (Playwright)
                "browser_navigate" => await BrowserToolImpl.BrowseUrlAsync(argsJson, ct).ConfigureAwait(false),
                "browser_click" => await BrowserToolImpl.ClickElementAsync(argsJson, ct).ConfigureAwait(false),
                "browser_type" => await BrowserToolImpl.TypeTextAsync(argsJson, ct).ConfigureAwait(false),
                "browser_get_elements" => await BrowserToolImpl.GetElementsAsync(argsJson, ct).ConfigureAwait(false),
                "browser_screenshot" => await BrowserToolImpl.ScreenshotAsync(argsJson, ct).ConfigureAwait(false),
                "browser_script" => await BrowserToolImpl.ExecuteScriptAsync(argsJson, ct).ConfigureAwait(false),
                "browser_close" => await BrowserToolImpl.CloseBrowserAsync().ConfigureAwait(false),
                
                _ => JsonSerializer.Serialize(new { error = $"Unknown tool: {name}" })
            };
        }
        
        /// <summary>
        /// Execute TypeScript code in Deno sandbox
        /// </summary>
        private static async Task<string> ExecuteCodeToolAsync(string argsJson, CancellationToken ct)
        {
            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                var root = doc.RootElement;
                
                if (!root.TryGetProperty("code", out var codeProp))
                    return JsonSerializer.Serialize(new { error = "Missing 'code' parameter" });
                    
                var code = codeProp.GetString() ?? "";
                var timeoutMs = root.TryGetProperty("timeout_ms", out var tp) ? tp.GetInt32() : 30000;
                
                // Check if MCP is enabled
                if (!McpConfig.Instance.Enabled)
                    return JsonSerializer.Serialize(new { error = "MCP code execution is disabled. Enable it with /mcp enable" });
                
                // Check if Deno is available
                if (!await McpCodeExecutor.IsDenoAvailableAsync())
                    return JsonSerializer.Serialize(new { error = "Deno runtime not found. Install Deno to use execute_code" });
                
                using var executor = new McpCodeExecutor();
                var result = await executor.ExecuteAsync(code, ct, TimeSpan.FromMilliseconds(timeoutMs));
                
                if (result.Success)
                {
                    return JsonSerializer.Serialize(new 
                    { 
                        success = true,
                        result = result.Result,
                        execution_time_ms = (int)result.Duration.TotalMilliseconds,
                        tool_calls = result.ToolCalls.Count
                    });
                }
                else
                {
                    return JsonSerializer.Serialize(new 
                    { 
                        success = false,
                        error = result.Error,
                        result = result.Result,
                        execution_time_ms = (int)result.Duration.TotalMilliseconds
                    });
                }
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { error = ex.Message });
            }
        }
    }
}
