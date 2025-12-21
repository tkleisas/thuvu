using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using thuvu.Models;

namespace thuvu
{
    /// <summary>
    /// Agent completion loop for LLM interactions with tool calling
    /// </summary>
    public static class AgentLoop
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
        
        private static void LogAgent(string msg)
        {
            var logLine = $"[{DateTime.Now:HH:mm:ss.fff}] [AGENT] {msg}";
            System.Diagnostics.Debug.WriteLine(logLine);
            try { File.AppendAllText("stream_debug.log", logLine + Environment.NewLine); } catch { }
        }

        // Loop detection settings
        public const int DefaultMaxIterations = 50;
        private const int MaxConsecutiveFailures = 3;
        
        /// <summary>
        /// Sends the current conversation to LM Studio. If the assistant requests tools,
        /// executes them, appends tool results, and repeats until a final answer is produced.
        /// Returns the assistant's final content.
        /// </summary>
        public static async Task<string?> CompleteWithToolsAsync(
            HttpClient http,
            string model,
            List<ChatMessage> messages,
            List<Tool> tools,
            CancellationToken ct,
            Action<string, string>? onToolResult = null,
            Action<string, string, TimeSpan>? onToolComplete = null,
            ToolProgressCallback? onToolProgress = null,
            int? maxIterations = null)
        {
            int maxIter = maxIterations ?? DefaultMaxIterations;
            int iteration = 0;
            var failureTracker = new Dictionary<string, int>(); // Track consecutive failures per tool
            
            while (true)
            {
                iteration++;
                if (iteration > maxIter)
                {
                    LogAgent($"Loop limit reached ({maxIter} iterations). Stopping.");
                    return $"[Agent stopped: Maximum iteration limit ({maxIter}) reached. The task may be too complex or the model is stuck in a loop.]";
                }
                var req = new ChatRequest
                {
                    Model = model,
                    Messages = messages,
                    Tools = tools,
                    ToolChoice = "auto",
                    Temperature = 0.2
                };
                
                // Log message summary for debugging
                LogAgent($"Sending {messages.Count} messages to API:");
                foreach (var m in messages)
                {
                    var preview = m.Content?.Length > 100 ? m.Content.Substring(0, 100) + "..." : m.Content ?? "(no content)";
                    LogAgent($"  [{m.Role}]: {preview}");
                }

                // Use retry handler for LLM API call
                var retryResult = await RetryHandler.ExecuteWithRetryAsync(
                    async (token) =>
                    {
                        using var resp = await http.PostAsJsonAsync("/v1/chat/completions", req, JsonOpts, token);
                        resp.EnsureSuccessStatusCode();
                        return await resp.Content.ReadFromJsonAsync<ChatResponse>(JsonOpts, token)
                               ?? throw new InvalidOperationException("Empty response.");
                    },
                    ct,
                    onRetry: (attempt, ex, delay) => RetryHandler.PrintRetryStatus(attempt, RetryHandler.DefaultConfig.MaxRetries, delay, ex.Message)
                );

                if (!retryResult.Success)
                {
                    throw retryResult.LastException ?? new InvalidOperationException("LLM request failed after retries");
                }

                var body = retryResult.Result!;
                if (body?.Usage is { } u)
                {
                    ConsoleHelpers.PrintTokenUsage(u.PromptTokens, u.CompletionTokens, u.TotalTokens);
                    // Update the effective token tracker (per-agent in orchestrated mode, global otherwise)
                    var tracker = Models.AgentContext.GetEffectiveTokenTracker();
                    tracker.UpdateFromUsage(u);
                    
                    // Check context size and auto-summarize if needed
                    if (tracker.AutoSummarizeEnabled && tracker.UsagePercent >= AutoSummarizeThreshold)
                    {
                        LogAgent($"Context at {tracker.UsagePercent:P0}, triggering auto-summarize");
                        await HandleContextSizeAsync(http, model, messages, ct);
                    }
                }
                var msg = body.Choices[0].Message;

                // Check if the LLM signaled task completion
                if (msg.Content?.Contains("thuvu Finished", StringComparison.OrdinalIgnoreCase) == true)
                {
                    LogAgent("Detected 'thuvu Finished' in response. Task complete.");
                    return msg.Content;
                }

                if (msg.ToolCalls is { Count: > 0 })
                {
                    messages.Add(msg);

                    foreach (var call in msg.ToolCalls)
                    {
                        var name = call.Function.Name;
                        var argsJson = call.Function.Arguments ?? "{}";
                        var toolStart = DateTime.Now;
                        var toolResult = await ToolExecutor.ExecuteToolAsync(name, argsJson, ct, onToolProgress);
                        var toolElapsed = DateTime.Now - toolStart;
                        ConsoleHelpers.PrintToolCall(name, argsJson, toolResult);
                        onToolResult?.Invoke(name, toolResult);
                        onToolComplete?.Invoke(name, toolResult, toolElapsed);
                        
                        // Track failures for loop detection
                        bool isFailure = toolResult.Contains("\"error\"") || 
                                        toolResult.Contains("\"timed_out\":true") ||
                                        toolResult.Contains("\"stderr\":\"timeout\"");
                        if (isFailure)
                        {
                            failureTracker[name] = failureTracker.GetValueOrDefault(name, 0) + 1;
                            if (failureTracker[name] >= MaxConsecutiveFailures)
                            {
                                LogAgent($"Tool {name} failed {MaxConsecutiveFailures} times consecutively. Stopping.");
                                return $"[Agent stopped: Tool '{name}' failed {MaxConsecutiveFailures} times consecutively. Please check the tool configuration or try a different approach.]";
                            }
                        }
                        else
                        {
                            failureTracker[name] = 0; // Reset on success
                        }

                        messages.Add(new ChatMessage(
                            role: "tool",
                            content: toolResult,
                            name: name,
                            toolCallId: call.Id
                        ));
                    }

                    continue;
                }

                return msg.Content;
            }
        }

        /// <summary>
        /// Like CompleteWithToolsAsync, but streams tokens for final answers.
        /// Prints tokens as they arrive via onToken (e.g., Console.Write).
        /// </summary>
        public static async Task<string?> CompleteWithToolsStreamingAsync(
            HttpClient http,
            string model,
            List<ChatMessage> messages,
            List<Tool> tools,
            CancellationToken ct,
            Action<string>? onToken = null,
            Action<string, string>? onToolResult = null,
            Action<Usage>? onUsage = null,
            Action<string, string, TimeSpan>? onToolComplete = null,
            ToolProgressCallback? onToolProgress = null,
            int? maxIterations = null)
        {
            int maxIter = maxIterations ?? DefaultMaxIterations;
            int iteration = 0;
            var failureTracker = new Dictionary<string, int>();
            var executedToolCalls = new HashSet<string>(); // Track tool call signatures to detect loops
            int noProgressCount = 0;
            const int MaxNoProgress = 3;
            
            while (true)
            {
                iteration++;
                if (iteration > maxIter)
                {
                    LogAgent($"Loop limit reached ({maxIter} iterations). Stopping.");
                    var msg = $"[Agent stopped: Maximum iteration limit ({maxIter}) reached. The task may be too complex or the model is stuck in a loop.]";
                    onToken?.Invoke(msg);
                    return msg;
                }
                
                var req = new ChatRequest
                {
                    Model = model,
                    Messages = messages,
                    Tools = tools,
                    ToolChoice = "auto",
                    Temperature = 0.2
                };
                
                // Log message summary for debugging
                LogAgent($"Sending {messages.Count} messages to API (iteration {iteration}):");
                foreach (var m in messages)
                {
                    var preview = m.Content?.Length > 100 ? m.Content.Substring(0, 100) + "..." : m.Content ?? "(no content)";
                    LogAgent($"  [{m.Role}]: {preview}");
                }

                LogAgent($"Calling StreamChatOnceAsync... (iteration {iteration})");
                var result = await StreamResult.StreamChatOnceAsync(http, req, ct, onToken, onUsage);
                LogAgent($"StreamChatOnceAsync returned, ToolCalls={result.ToolCalls?.Count ?? 0}, Content length={result.Content?.Length ?? 0}");

                // Check context size and auto-summarize if needed
                var tracker = Models.AgentContext.GetEffectiveTokenTracker();
                if (tracker.AutoSummarizeEnabled && tracker.UsagePercent >= AutoSummarizeThreshold)
                {
                    LogAgent($"Context at {tracker.UsagePercent:P0}, triggering auto-summarize");
                    await HandleContextSizeAsync(http, model, messages, ct, s => onToken?.Invoke($"\n[{s}]\n"));
                }

                // Check if the LLM signaled task completion (various formats)
                bool hasFinishSignal = result.Content != null && 
                    (result.Content.Contains("thuvu Finished", StringComparison.OrdinalIgnoreCase) ||
                     result.Content.Contains("Finished Tasks", StringComparison.OrdinalIgnoreCase) ||
                     result.Content.Contains("Task complete", StringComparison.OrdinalIgnoreCase) ||
                     result.Content.Contains("successfully created", StringComparison.OrdinalIgnoreCase) ||
                     result.Content.Contains("I have successfully", StringComparison.OrdinalIgnoreCase));
                
                // If finish signal detected and no tool calls, we're done
                if (hasFinishSignal && (result.ToolCalls == null || result.ToolCalls.Count == 0))
                {
                    LogAgent("Detected task completion signal with no pending tool calls.");
                    return result.Content;
                }
                
                // If finish signal detected but there are tool calls, log warning and finish anyway
                if (hasFinishSignal)
                {
                    LogAgent("Detected task completion signal. Ignoring further tool calls and finishing.");
                    return result.Content;
                }
                
                if (result.ToolCalls is { Count: > 0 })
                {
                    LogAgent($"Processing {result.ToolCalls.Count} tool calls");
                    
                    // Check for repeated tool calls (loop detection)
                    bool allCallsRepeated = true;
                    foreach (var call in result.ToolCalls)
                    {
                        var callSignature = $"{call.Function.Name}:{call.Function.Arguments}";
                        if (!executedToolCalls.Contains(callSignature))
                        {
                            allCallsRepeated = false;
                            break;
                        }
                    }
                    
                    if (allCallsRepeated)
                    {
                        noProgressCount++;
                        LogAgent($"All tool calls are repeats ({noProgressCount}/{MaxNoProgress})");
                        if (noProgressCount >= MaxNoProgress)
                        {
                            LogAgent("Model stuck repeating same tool calls. Stopping.");
                            return result.Content + "\n\n[Agent stopped: Model stuck in tool call loop.]";
                        }
                    }
                    else
                    {
                        noProgressCount = 0;
                    }
                    
                    messages.Add(new ChatMessage
                    {
                        Role = "assistant",
                        Content = result.Content, // Preserve any content along with tool calls
                        ToolCalls = result.ToolCalls
                    });

                    foreach (var call in result.ToolCalls)
                    {
                        var name = call.Function.Name;
                        var argsJson = call.Function.Arguments ?? "{}";
                        var callSignature = $"{name}:{argsJson}";
                        executedToolCalls.Add(callSignature);
                        
                        LogAgent($"Executing tool: {name}");
                        var toolStart = DateTime.Now;
                        var toolResult = await ToolExecutor.ExecuteToolAsync(name, argsJson, ct, onToolProgress);
                        var toolElapsed = DateTime.Now - toolStart;
                        LogAgent($"Tool {name} completed in {toolElapsed.TotalSeconds:F1}s, result length={toolResult.Length}");
                        ConsoleHelpers.PrintToolCall(name, argsJson, toolResult);
                        onToolResult?.Invoke(name, toolResult);
                        onToolComplete?.Invoke(name, toolResult, toolElapsed);
                        
                        // Track failures for loop detection
                        bool isFailure = toolResult.Contains("\"error\"") || 
                                        toolResult.Contains("\"timed_out\":true") ||
                                        toolResult.Contains("\"stderr\":\"timeout\"");
                        if (isFailure)
                        {
                            failureTracker[name] = failureTracker.GetValueOrDefault(name, 0) + 1;
                            if (failureTracker[name] >= MaxConsecutiveFailures)
                            {
                                LogAgent($"Tool {name} failed {MaxConsecutiveFailures} times consecutively. Stopping.");
                                var msg = $"[Agent stopped: Tool '{name}' failed {MaxConsecutiveFailures} times consecutively. Please check the tool or try a different approach.]";
                                onToken?.Invoke(msg);
                                return msg;
                            }
                        }
                        else
                        {
                            failureTracker[name] = 0; // Reset on success
                        }

                        messages.Add(new ChatMessage(
                            role: "tool",
                            content: toolResult,
                            name: name,
                            toolCallId: call.Id
                        ));
                    }

                    LogAgent("Continuing loop for next LLM response...");
                    continue;
                }

                LogAgent($"Returning final content, length={result.Content?.Length ?? 0}");
                return result.Content;
            }
        }

        /// <summary>
        /// Auto-summarize threshold (90% of context)
        /// </summary>
        private const double AutoSummarizeThreshold = 0.90;
        
        /// <summary>
        /// Truncation threshold after summarization fails (95% of context)
        /// </summary>
        private const double TruncationThreshold = 0.95;

        /// <summary>
        /// Summarize the conversation to reduce context size.
        /// Keeps the system prompt and creates a summary of the conversation so far.
        /// </summary>
        public static async Task<bool> SummarizeConversationAsync(
            HttpClient http,
            string model,
            List<ChatMessage> messages,
            CancellationToken ct,
            Action<string>? onStatus = null)
        {
            if (messages.Count < 3) // Need at least system + some conversation
            {
                LogAgent("Not enough messages to summarize");
                return false;
            }

            onStatus?.Invoke("Auto-summarizing conversation to reduce context size...");
            LogAgent($"Starting auto-summarization. Current message count: {messages.Count}");

            try
            {
                // Build conversation text for summarization (skip system message)
                var conversationParts = new List<string>();
                int systemMsgIndex = -1;
                
                for (int i = 0; i < messages.Count; i++)
                {
                    var msg = messages[i];
                    if (msg.Role == "system")
                    {
                        systemMsgIndex = i;
                        continue;
                    }
                    
                    var roleLabel = msg.Role switch
                    {
                        "user" => "User",
                        "assistant" => "Assistant",
                        "tool" => $"Tool({msg.Name})",
                        _ => msg.Role
                    };
                    
                    // Truncate very long messages for summary
                    var content = msg.Content ?? "";
                    if (content.Length > 2000)
                        content = content.Substring(0, 2000) + "...[truncated]";
                    
                    conversationParts.Add($"{roleLabel}: {content}");
                }

                var conversationText = string.Join("\n\n", conversationParts);
                
                // Create summarization request
                var summaryRequest = new ChatRequest
                {
                    Model = model,
                    Messages = new List<ChatMessage>
                    {
                        new("system", "You are a helpful assistant that summarizes conversations. Create a concise summary that preserves all important context, decisions made, files modified, errors encountered, and current task status. Be thorough but brief."),
                        new("user", $"Summarize this conversation, preserving key context for continuing the task:\n\n{conversationText}")
                    },
                    Temperature = 0.3
                };

                using var resp = await http.PostAsJsonAsync("/v1/chat/completions", summaryRequest, JsonOpts, ct);
                resp.EnsureSuccessStatusCode();
                var body = await resp.Content.ReadFromJsonAsync<ChatResponse>(JsonOpts, ct);
                
                var summary = body?.Choices?[0]?.Message?.Content;
                if (string.IsNullOrEmpty(summary))
                {
                    LogAgent("Summarization returned empty result");
                    return false;
                }

                LogAgent($"Generated summary ({summary.Length} chars)");

                // Preserve system message if exists
                var systemMsg = systemMsgIndex >= 0 ? messages[systemMsgIndex] : null;
                
                // Clear and rebuild messages with summary
                messages.Clear();
                if (systemMsg != null)
                    messages.Add(systemMsg);
                
                // Add summary as context
                messages.Add(new ChatMessage("user", $"[CONVERSATION SUMMARY - Context from previous messages]\n{summary}\n\n[END SUMMARY - Continue from here]"));
                messages.Add(new ChatMessage("assistant", "I understand. I have the context from the summarized conversation. I'll continue from where we left off."));

                LogAgent($"Conversation summarized. New message count: {messages.Count}");
                onStatus?.Invoke($"Conversation summarized. Reduced from many messages to {messages.Count}.");
                
                return true;
            }
            catch (Exception ex)
            {
                LogAgent($"Summarization failed: {ex.Message}");
                onStatus?.Invoke($"Summarization failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Truncate older messages while keeping system prompt and recent context.
        /// This is a last resort when summarization doesn't help enough.
        /// </summary>
        public static void TruncateConversation(List<ChatMessage> messages, int keepRecentCount = 6, Action<string>? onStatus = null)
        {
            if (messages.Count <= keepRecentCount + 1) // +1 for system message
            {
                LogAgent("Not enough messages to truncate");
                return;
            }

            LogAgent($"Truncating conversation. Current count: {messages.Count}, keeping {keepRecentCount} recent");
            onStatus?.Invoke($"Truncating conversation to last {keepRecentCount} messages...");

            // Find system message
            ChatMessage? systemMsg = null;
            int systemIndex = -1;
            for (int i = 0; i < messages.Count; i++)
            {
                if (messages[i].Role == "system")
                {
                    systemMsg = messages[i];
                    systemIndex = i;
                    break;
                }
            }

            // Get recent messages (excluding system)
            var nonSystemMessages = new List<ChatMessage>();
            for (int i = 0; i < messages.Count; i++)
            {
                if (i != systemIndex)
                    nonSystemMessages.Add(messages[i]);
            }

            // Keep only recent messages
            var recentMessages = nonSystemMessages.Count > keepRecentCount
                ? nonSystemMessages.GetRange(nonSystemMessages.Count - keepRecentCount, keepRecentCount)
                : nonSystemMessages;

            // Rebuild messages
            messages.Clear();
            if (systemMsg != null)
                messages.Add(systemMsg);
            
            // Add truncation notice
            messages.Add(new ChatMessage("user", "[Note: Earlier conversation was truncated due to context limits. Continue with the current task.]"));
            messages.Add(new ChatMessage("assistant", "Understood. I'll continue working on the current task."));
            
            messages.AddRange(recentMessages);

            LogAgent($"Conversation truncated. New count: {messages.Count}");
            onStatus?.Invoke($"Conversation truncated to {messages.Count} messages.");
        }

        /// <summary>
        /// Check and handle context size, auto-summarizing or truncating if needed.
        /// Returns true if context was modified.
        /// </summary>
        public static async Task<bool> HandleContextSizeAsync(
            HttpClient http,
            string model,
            List<ChatMessage> messages,
            CancellationToken ct,
            Action<string>? onStatus = null)
        {
            var tracker = Models.AgentContext.GetEffectiveTokenTracker();
            
            if (!tracker.AutoSummarizeEnabled)
                return false;

            // Check if we need to take action
            if (tracker.UsagePercent < AutoSummarizeThreshold)
                return false;

            LogAgent($"Context usage at {tracker.UsagePercent:P0}, threshold is {AutoSummarizeThreshold:P0}");
            onStatus?.Invoke($"⚠️ Context usage at {tracker.UsagePercent:P0}, attempting to reduce...");

            // First try summarization
            var summarized = await SummarizeConversationAsync(http, model, messages, ct, onStatus);
            
            // If still over truncation threshold, truncate
            if (tracker.UsagePercent >= TruncationThreshold)
            {
                LogAgent($"Still at {tracker.UsagePercent:P0} after summarization, truncating");
                TruncateConversation(messages, keepRecentCount: 4, onStatus);
            }

            return summarized || tracker.UsagePercent >= TruncationThreshold;
        }

        /// <summary>
        /// Get context length from LM Studio API
        /// </summary>
        public static async Task<int?> GetContextLengthAsync(HttpClient http, string modelId, CancellationToken ct)
        {
            try
            {
                using var resp = await http.GetAsync($"/api/v0/models/{Uri.EscapeDataString(modelId)}", ct);
                if (!resp.IsSuccessStatusCode) return null;

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                var root = doc.RootElement;

                if (root.TryGetProperty("max_context_length", out var m) && m.ValueKind == JsonValueKind.Number)
                    return m.GetInt32();

                // Fallback if the API shape changes
                if (root.TryGetProperty("model_info", out var mi) &&
                    mi.ValueKind == JsonValueKind.Object &&
                    mi.TryGetProperty("context_length", out var cl) &&
                    cl.ValueKind == JsonValueKind.Number)
                    return cl.GetInt32();
            }
            catch { }

            return null;
        }
    }
}
