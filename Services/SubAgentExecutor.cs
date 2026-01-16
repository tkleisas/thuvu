using System.Diagnostics;
using System.Text.Json;
using thuvu.Models;
using thuvu.Tools;

namespace thuvu.Services
{
    /// <summary>
    /// Result from a sub-agent execution.
    /// </summary>
    public class SubAgentResult
    {
        public bool Success { get; set; }
        public string Status { get; set; } = "completed"; // completed, failed, timeout, cancelled
        public string Summary { get; set; } = "";
        public string? Details { get; set; }
        public List<string> FilesModified { get; set; } = new();
        public List<string> FilesCreated { get; set; } = new();
        public List<string> Suggestions { get; set; } = new();
        public string? ErrorMessage { get; set; }
        public int IterationCount { get; set; }
        public long DurationMs { get; set; }
        public string? BailoutReason { get; set; }
    }

    /// <summary>
    /// Context for a sub-agent execution.
    /// </summary>
    public class SubAgentContext
    {
        public string SessionId { get; set; } = "";
        public long? ParentMessageId { get; set; }
        public string Role { get; set; } = "";
        public string Task { get; set; } = "";
        public List<string>? ContextFiles { get; set; }
        public string? SuccessCriteria { get; set; }
        public int CurrentDepth { get; set; }
        public List<ChatMessage> ParentContext { get; set; } = new();
    }

    /// <summary>
    /// Executes sub-agent tasks with specialized roles.
    /// </summary>
    public class SubAgentExecutor
    {
        private readonly HttpClient _httpClient;
        
        // Track file modifications during execution
        private readonly HashSet<string> _filesModified = new();
        private readonly HashSet<string> _filesCreated = new();
        private int _iterationCount = 0;
        
        public SubAgentExecutor(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        /// <summary>
        /// Execute a sub-agent task synchronously.
        /// </summary>
        public async Task<SubAgentResult> ExecuteAsync(SubAgentContext context, CancellationToken ct = default)
        {
            var rolesConfig = AgentRolesRegistry.Instance;
            
            // Check if delegation is enabled
            if (!rolesConfig.Enabled)
            {
                return new SubAgentResult
                {
                    Success = false,
                    Status = "failed",
                    ErrorMessage = "Sub-agent delegation is not enabled. Set AgentRoles.Enabled = true in config."
                };
            }

            // Get role definition
            var roleDefinition = rolesConfig.GetRole(context.Role);
            if (roleDefinition == null)
            {
                return new SubAgentResult
                {
                    Success = false,
                    Status = "failed",
                    ErrorMessage = $"Unknown role: {context.Role}"
                };
            }

            // Check depth limit
            if (context.CurrentDepth >= rolesConfig.MaxDepth)
            {
                return new SubAgentResult
                {
                    Success = false,
                    Status = "failed",
                    ErrorMessage = $"Maximum delegation depth ({rolesConfig.MaxDepth}) exceeded"
                };
            }

            var sw = Stopwatch.StartNew();
            var result = new SubAgentResult();
            _filesModified.Clear();
            _filesCreated.Clear();
            _iterationCount = 0;

            try
            {
                AgentLogger.LogInfo("Starting sub-agent execution: role={Role}, task={Task}", 
                    context.Role, context.Task.Length > 100 ? context.Task[..100] + "..." : context.Task);

                // Record the start of this sub-agent execution
                var messageRecord = new MessageRecord
                {
                    SessionId = context.SessionId,
                    ParentMessageId = context.ParentMessageId,
                    StartedAt = DateTime.Now,
                    AgentRole = context.Role,
                    AgentDepth = context.CurrentDepth + 1,
                    ModelId = roleDefinition.ModelId ?? AgentConfig.Config.Model,
                    SystemPromptId = roleDefinition.RoleId,
                    MessageType = "delegation",
                    RequestContent = context.Task,
                    ContextMode = roleDefinition.ContextMode,
                    MaxIterations = roleDefinition.MaxIterations,
                    MaxDurationMs = roleDefinition.MaxDurationMs,
                    Status = "running"
                };

                var messageId = await SqliteService.Instance.StartMessageAsync(messageRecord, ct);

                // Build messages for sub-agent
                var messages = BuildSubAgentMessages(context, roleDefinition);
                
                // Get tools (sub-agents get same tools as parent for now)
                var tools = BuildTools.GetBuildTools();
                
                // Remove delegate_to_agent if at max depth or role cannot delegate
                if (context.CurrentDepth + 1 >= rolesConfig.MaxDepth - 1 || !roleDefinition.CanDelegate)
                {
                    tools = tools.Where(t => t.Function?.Name != "delegate_to_agent").ToList();
                }

                // Get the appropriate HttpClient for this model
                var modelId = roleDefinition.ModelId ?? AgentConfig.Config.Model;
                var modelConfig = ModelRegistry.Instance?.GetModel(modelId);
                var httpClient = modelConfig?.CreateHttpClient() ?? _httpClient;

                // Execute using AgentLoop with timeout
                var maxDuration = TimeSpan.FromMilliseconds(roleDefinition.MaxDurationMs);
                using var timeoutCts = new CancellationTokenSource(maxDuration);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                string? response = null;
                try
                {
                    response = await AgentLoop.CompleteWithToolsAsync(
                        httpClient,
                        modelId,
                        messages,
                        tools,
                        linkedCts.Token,
                        onToolResult: (name, res) => TrackToolResult(name, res),
                        onToolCall: (name, args) => TrackToolCall(name, args),
                        maxIterations: roleDefinition.MaxIterations
                    );
                    
                    result.Status = "completed";
                    result.Success = true;
                }
                catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested)
                {
                    result.Status = "timeout";
                    result.BailoutReason = "max_duration";
                    result.Success = false;
                    result.Summary = $"Sub-agent timed out after {sw.ElapsedMilliseconds}ms";
                }
                catch (OperationCanceledException)
                {
                    result.Status = "cancelled";
                    result.BailoutReason = "user_cancelled";
                    result.Success = false;
                }

                // Check if we hit iteration limit (indicated by special message)
                if (response?.Contains("Maximum iteration limit") == true)
                {
                    result.Status = "timeout";
                    result.BailoutReason = "max_iterations";
                    result.Success = true; // Partial success
                }

                result.Summary = ExtractSummary(response, context.Task);
                result.Details = response;
                result.FilesModified = _filesModified.ToList();
                result.FilesCreated = _filesCreated.ToList();
                result.IterationCount = _iterationCount;

                sw.Stop();
                result.DurationMs = sw.ElapsedMilliseconds;

                // Complete the message record
                await SqliteService.Instance.CompleteMessageAsync(messageId, new MessageCompleteInfo
                {
                    CompletedAt = DateTime.Now,
                    DurationMs = result.DurationMs,
                    ResponseContent = result.Details,
                    ResponseSummary = result.Summary,
                    FilesModifiedJson = result.FilesModified.Count > 0 
                        ? JsonSerializer.Serialize(result.FilesModified) : null,
                    FilesCreatedJson = result.FilesCreated.Count > 0 
                        ? JsonSerializer.Serialize(result.FilesCreated) : null,
                    IterationNumber = result.IterationCount
                }, ct);

                AgentLogger.LogInfo("Sub-agent completed: role={Role}, status={Status}, iterations={Iterations}, duration={Duration}ms",
                    context.Role, result.Status, result.IterationCount, result.DurationMs);

                return result;
            }
            catch (Exception ex)
            {
                sw.Stop();
                AgentLogger.LogError("Sub-agent failed: role={Role}, error={Error}", context.Role, ex.Message);
                
                return new SubAgentResult
                {
                    Success = false,
                    Status = "failed",
                    ErrorMessage = ex.Message,
                    DurationMs = sw.ElapsedMilliseconds
                };
            }
        }

        /// <summary>
        /// Track tool calls for iteration counting.
        /// </summary>
        private void TrackToolCall(string toolName, string argsJson)
        {
            _iterationCount++;
        }

        /// <summary>
        /// Track tool results for file modification tracking.
        /// </summary>
        private void TrackToolResult(string toolName, string resultJson)
        {
            // Try to extract file path from common patterns
            try
            {
                if (toolName == "write_file" || toolName == "apply_patch")
                {
                    using var doc = JsonDocument.Parse(resultJson);
                    if (doc.RootElement.TryGetProperty("path", out var pathProp) ||
                        doc.RootElement.TryGetProperty("file", out pathProp))
                    {
                        var path = pathProp.GetString();
                        if (!string.IsNullOrEmpty(path))
                        {
                            if (resultJson.Contains("\"created\":true") || resultJson.Contains("\"new_file\":true"))
                                _filesCreated.Add(path);
                            else
                                _filesModified.Add(path);
                        }
                    }
                }
            }
            catch
            {
                // Ignore parsing errors
            }
        }

        /// <summary>
        /// Build the message list for a sub-agent.
        /// </summary>
        private List<ChatMessage> BuildSubAgentMessages(SubAgentContext context, AgentRoleDefinition roleDefinition)
        {
            var messages = new List<ChatMessage>();

            // Load role-specific system prompt
            var systemPrompt = roleDefinition.LoadSystemPrompt();
            if (string.IsNullOrEmpty(systemPrompt))
            {
                // Fallback to default prompt
                systemPrompt = $"You are a {roleDefinition.DisplayName}. {roleDefinition.Description}";
            }

            // Add sub-agent awareness
            systemPrompt += $"\n\n## Sub-Agent Context\nYou are operating as a sub-agent at depth {context.CurrentDepth + 1}. " +
                           "Focus on completing the assigned task efficiently. " +
                           "Return a clear summary of what you accomplished.";

            messages.Add(new ChatMessage("system", systemPrompt));

            // Add relevant parent context based on context mode
            if (roleDefinition.ContextMode == "full" && context.ParentContext.Count > 0)
            {
                // Include recent parent context (last few exchanges)
                var recentContext = context.ParentContext
                    .Where(m => m.Role != "system")
                    .TakeLast(10)
                    .ToList();

                if (recentContext.Count > 0)
                {
                    var contextSummary = "## Recent Context from Parent Agent\n";
                    foreach (var msg in recentContext)
                    {
                        var content = msg.Content?.Length > 500 
                            ? msg.Content[..500] + "..." 
                            : msg.Content ?? "";
                        contextSummary += $"[{msg.Role}]: {content}\n";
                    }
                    messages.Add(new ChatMessage("user", contextSummary));
                }
            }

            // Build the task message
            var taskMessage = $"## Assigned Task\n{context.Task}";
            
            if (context.ContextFiles?.Count > 0)
            {
                taskMessage += $"\n\n## Focus Files\nPlease focus on these files:\n- " + 
                              string.Join("\n- ", context.ContextFiles);
            }
            
            if (!string.IsNullOrEmpty(context.SuccessCriteria))
            {
                taskMessage += $"\n\n## Success Criteria\n{context.SuccessCriteria}";
            }

            messages.Add(new ChatMessage("user", taskMessage));

            return messages;
        }

        /// <summary>
        /// Extract a summary from the last assistant response.
        /// </summary>
        private string ExtractSummary(string? response, string task)
        {
            if (string.IsNullOrEmpty(response))
                return $"Completed task: {(task.Length > 100 ? task[..100] + "..." : task)}";

            // Look for a summary section in the response
            var lines = response.Split('\n');
            var summaryLines = new List<string>();
            var inSummary = false;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("## Summary", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("# Summary", StringComparison.OrdinalIgnoreCase))
                {
                    inSummary = true;
                    continue;
                }
                if (inSummary)
                {
                    if (trimmed.StartsWith("#"))
                        break;
                    if (!string.IsNullOrWhiteSpace(trimmed))
                        summaryLines.Add(trimmed);
                }
            }

            if (summaryLines.Count > 0)
                return string.Join(" ", summaryLines);

            // Fallback: first paragraph
            var firstParagraph = response.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (firstParagraph != null && firstParagraph.Length > 300)
                return firstParagraph[..300] + "...";
            
            return firstParagraph ?? response[..Math.Min(300, response.Length)];
        }
    }
}
