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
                
                // Vision/Image Analysis
                "analyze_image" => await VisionToolImpl.AnalyzeImageAsync(argsJson, ct).ConfigureAwait(false),
                
                // UI Automation
                "ui_capture" => await Tools.UIAutomation.UIAutomationToolImpl.CaptureAsync(argsJson, ct).ConfigureAwait(false),
                "ui_list_windows" => await Tools.UIAutomation.UIAutomationToolImpl.ListWindowsAsync(argsJson, ct).ConfigureAwait(false),
                "ui_focus_window" => await Tools.UIAutomation.UIAutomationToolImpl.FocusWindowAsync(argsJson, ct).ConfigureAwait(false),
                "ui_click" => await Tools.UIAutomation.UIAutomationToolImpl.ClickAsync(argsJson, ct).ConfigureAwait(false),
                "ui_type" => await Tools.UIAutomation.UIAutomationToolImpl.TypeAsync(argsJson, ct).ConfigureAwait(false),
                "ui_mouse_move" => await Tools.UIAutomation.UIAutomationToolImpl.MoveMouseAsync(argsJson, ct).ConfigureAwait(false),
                "ui_get_element" => await Tools.UIAutomation.UIAutomationToolImpl.GetElementAsync(argsJson, ct).ConfigureAwait(false),
                "ui_wait" => await Tools.UIAutomation.UIAutomationToolImpl.WaitAsync(argsJson, ct).ConfigureAwait(false),
                
                // Process Management tools
                "process_start" => await Tools.ProcessManagement.ProcessToolImpl.ProcessStartAsync(argsJson).ConfigureAwait(false),
                "process_read" => await Tools.ProcessManagement.ProcessToolImpl.ProcessReadAsync(argsJson).ConfigureAwait(false),
                "process_write" => await Tools.ProcessManagement.ProcessToolImpl.ProcessWriteAsync(argsJson).ConfigureAwait(false),
                "process_status" => await Tools.ProcessManagement.ProcessToolImpl.ProcessStatusAsync(argsJson).ConfigureAwait(false),
                "process_stop" => await Tools.ProcessManagement.ProcessToolImpl.ProcessStopAsync(argsJson).ConfigureAwait(false),
                
                // Code Indexing & Context tools (SQLite)
                "code_index" => await ExecuteCodeIndexAsync(argsJson, ct).ConfigureAwait(false),
                "code_query" => await ExecuteCodeQueryAsync(argsJson, ct).ConfigureAwait(false),
                "context_store" => await ExecuteContextStoreAsync(argsJson, ct).ConfigureAwait(false),
                "context_get" => await ExecuteContextGetAsync(argsJson, ct).ConfigureAwait(false),
                "index_stats" => await SqliteToolImpl.IndexStatsAsync(ct).ConfigureAwait(false),
                "index_clear" => await SqliteToolImpl.IndexClearAsync(ct).ConfigureAwait(false),

                // Agent Communication tools
                "agent_list" => await AgentCommunicationToolImpl.AgentListAsync(ct).ConfigureAwait(false),
                "agent_submit" => await ExecuteAgentSubmitAsync(argsJson, ct).ConfigureAwait(false),
                "agent_status" => await ExecuteAgentStatusAsync(argsJson, ct).ConfigureAwait(false),
                "agent_result" => await ExecuteAgentResultAsync(argsJson, ct).ConfigureAwait(false),
                "agent_cancel" => await ExecuteAgentCancelAsync(argsJson, ct).ConfigureAwait(false),
                
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
                var timeoutMs = root.TryGetProperty("timeout_ms", out var tp) 
                    ? tp.GetInt32() 
                    : McpConfig.Instance.DefaultTimeout;
                
                // Check if MCP is enabled
                if (!McpConfig.Instance.Enabled)
                    return JsonSerializer.Serialize(new { 
                        success = false,
                        error = "MCP code execution is disabled. Enable it with /mcp enable" 
                    });
                
                // Check if Deno is available
                if (!await McpCodeExecutor.IsDenoAvailableAsync())
                    return JsonSerializer.Serialize(new { 
                        success = false,
                        error = "Deno runtime not found. Install Deno to use execute_code.",
                        hint = "Install from https://deno.land or run: irm https://deno.land/install.ps1 | iex"
                    });
                
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
                        execution_time_ms = (int)result.Duration.TotalMilliseconds,
                        hint = result.Error?.Contains("Module not found") == true 
                            ? "MCP runtime not found. Ensure 'mcp/runtime/sandbox.ts' exists relative to the thuvu executable."
                            : null
                    });
                }
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { 
                    success = false,
                    error = ex.Message 
                });
            }
        }
        
        /// <summary>
        /// Execute code_index tool
        /// </summary>
        private static async Task<string> ExecuteCodeIndexAsync(string argsJson, CancellationToken ct)
        {
            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                var root = doc.RootElement;
                
                var path = root.TryGetProperty("path", out var pathProp) ? pathProp.GetString() ?? "." : ".";
                var force = root.TryGetProperty("force", out var forceProp) && forceProp.GetBoolean();
                
                return await SqliteToolImpl.CodeIndexAsync(path, force, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { success = false, error = ex.Message });
            }
        }
        
        /// <summary>
        /// Execute code_query tool
        /// </summary>
        private static async Task<string> ExecuteCodeQueryAsync(string argsJson, CancellationToken ct)
        {
            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                var root = doc.RootElement;
                
                var search = root.TryGetProperty("search", out var searchProp) ? searchProp.GetString() : null;
                var kind = root.TryGetProperty("kind", out var kindProp) ? kindProp.GetString() : null;
                var file = root.TryGetProperty("file", out var fileProp) ? fileProp.GetString() : null;
                var symbolId = root.TryGetProperty("symbol_id", out var idProp) ? idProp.GetInt64() : (long?)null;
                var findRefs = root.TryGetProperty("find_references", out var refsProp) && refsProp.GetBoolean();
                var limit = root.TryGetProperty("limit", out var limitProp) ? limitProp.GetInt32() : 50;
                
                return await SqliteToolImpl.CodeQueryAsync(search, kind, file, symbolId, findRefs, limit, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { success = false, error = ex.Message });
            }
        }
        
        /// <summary>
        /// Execute context_store tool
        /// </summary>
        private static async Task<string> ExecuteContextStoreAsync(string argsJson, CancellationToken ct)
        {
            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                var root = doc.RootElement;
                
                if (!root.TryGetProperty("key", out var keyProp))
                    return JsonSerializer.Serialize(new { success = false, error = "Missing 'key' parameter" });
                if (!root.TryGetProperty("value", out var valueProp))
                    return JsonSerializer.Serialize(new { success = false, error = "Missing 'value' parameter" });
                
                var key = keyProp.GetString() ?? "";
                var value = valueProp.GetString() ?? "";
                var category = root.TryGetProperty("category", out var catProp) ? catProp.GetString() : null;
                var projectPath = root.TryGetProperty("project_path", out var projProp) ? projProp.GetString() : null;
                var expiresInDays = root.TryGetProperty("expires_in_days", out var expProp) ? expProp.GetInt32() : (int?)null;
                
                return await SqliteToolImpl.ContextStoreAsync(key, value, category, projectPath, expiresInDays, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { success = false, error = ex.Message });
            }
        }
        
        /// <summary>
        /// Execute context_get tool
        /// </summary>
        private static async Task<string> ExecuteContextGetAsync(string argsJson, CancellationToken ct)
        {
            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                var root = doc.RootElement;
                
                var keyPattern = root.TryGetProperty("key_pattern", out var keyProp) ? keyProp.GetString() : null;
                var category = root.TryGetProperty("category", out var catProp) ? catProp.GetString() : null;
                var projectPath = root.TryGetProperty("project_path", out var projProp) ? projProp.GetString() : null;
                var limit = root.TryGetProperty("limit", out var limitProp) ? limitProp.GetInt32() : 50;
                
                return await SqliteToolImpl.ContextGetAsync(keyPattern, category, projectPath, limit, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { success = false, error = ex.Message });
            }
        }

        #region Agent Communication Helpers

        private static async Task<string> ExecuteAgentSubmitAsync(string argsJson, CancellationToken ct)
        {
            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                var root = doc.RootElement;
                
                if (!root.TryGetProperty("agent_name", out var nameProp))
                    return JsonSerializer.Serialize(new { success = false, error = "Missing 'agent_name' parameter" });
                if (!root.TryGetProperty("prompt", out var promptProp))
                    return JsonSerializer.Serialize(new { success = false, error = "Missing 'prompt' parameter" });
                
                var agentName = nameProp.GetString() ?? "";
                var prompt = promptProp.GetString() ?? "";
                
                return await AgentCommunicationToolImpl.AgentSubmitAsync(agentName, prompt, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { success = false, error = ex.Message });
            }
        }

        private static async Task<string> ExecuteAgentStatusAsync(string argsJson, CancellationToken ct)
        {
            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                var root = doc.RootElement;
                
                if (!root.TryGetProperty("agent_name", out var nameProp))
                    return JsonSerializer.Serialize(new { success = false, error = "Missing 'agent_name' parameter" });
                
                var agentName = nameProp.GetString() ?? "";
                
                return await AgentCommunicationToolImpl.AgentStatusAsync(agentName, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { success = false, error = ex.Message });
            }
        }

        private static async Task<string> ExecuteAgentResultAsync(string argsJson, CancellationToken ct)
        {
            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                var root = doc.RootElement;
                
                if (!root.TryGetProperty("agent_name", out var nameProp))
                    return JsonSerializer.Serialize(new { success = false, error = "Missing 'agent_name' parameter" });
                if (!root.TryGetProperty("job_id", out var jobProp))
                    return JsonSerializer.Serialize(new { success = false, error = "Missing 'job_id' parameter" });
                
                var agentName = nameProp.GetString() ?? "";
                var jobId = jobProp.GetString() ?? "";
                
                return await AgentCommunicationToolImpl.AgentResultAsync(agentName, jobId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { success = false, error = ex.Message });
            }
        }

        private static async Task<string> ExecuteAgentCancelAsync(string argsJson, CancellationToken ct)
        {
            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                var root = doc.RootElement;
                
                if (!root.TryGetProperty("agent_name", out var nameProp))
                    return JsonSerializer.Serialize(new { success = false, error = "Missing 'agent_name' parameter" });
                if (!root.TryGetProperty("job_id", out var jobProp))
                    return JsonSerializer.Serialize(new { success = false, error = "Missing 'job_id' parameter" });
                
                var agentName = nameProp.GetString() ?? "";
                var jobId = jobProp.GetString() ?? "";
                
                return await AgentCommunicationToolImpl.AgentCancelAsync(agentName, jobId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { success = false, error = ex.Message });
            }
        }

        #endregion
    }
}
