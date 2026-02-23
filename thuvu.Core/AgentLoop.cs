using System;
using System.Collections.Generic;
using System.Linq;
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
        public const int DefaultMaxIterations = 50; // Fallback default
        private const int MaxConsecutiveFailures = 10;

        /// <summary>
        /// Attempt to parse inline tool calls from content text.
        /// Some models (especially via OpenRouter) emit tool calls as plain text like:
        /// read_file{"path": "file.cs"} instead of using proper tool_calls JSON.
        /// Returns the parsed tool calls and the content with inline calls stripped out.
        /// </summary>
        private static (List<ToolCall>? Calls, string? CleanedContent) TryParseInlineToolCalls(string? content, List<Tool> tools)
        {
            if (string.IsNullOrEmpty(content)) return (null, content);

            var toolNames = new HashSet<string>(tools.Select(t => t.Function.Name));
            var result = new List<ToolCall>();
            // Collect (start, length) spans to remove from content
            var spans = new List<(int Start, int Length)>();

            // Find each tool name followed by '{' and extract the full JSON object
            foreach (var name in toolNames)
            {
                int searchFrom = 0;
                while (searchFrom < content.Length)
                {
                    int nameIdx = content.IndexOf(name, searchFrom, StringComparison.Ordinal);
                    if (nameIdx < 0) break;

                    // Verify it's a word boundary (not part of a larger word)
                    if (nameIdx > 0 && char.IsLetterOrDigit(content[nameIdx - 1]))
                    {
                        searchFrom = nameIdx + name.Length;
                        continue;
                    }

                    // Skip whitespace after tool name to find '{'
                    int braceStart = nameIdx + name.Length;
                    while (braceStart < content.Length && char.IsWhiteSpace(content[braceStart]))
                        braceStart++;

                    if (braceStart >= content.Length || content[braceStart] != '{')
                    {
                        searchFrom = nameIdx + name.Length;
                        continue;
                    }

                    // Extract JSON by counting braces, respecting string literals
                    int? braceEnd = FindMatchingBrace(content, braceStart);
                    if (braceEnd == null)
                    {
                        searchFrom = nameIdx + name.Length;
                        continue;
                    }

                    var argsJson = content.Substring(braceStart, braceEnd.Value - braceStart + 1);
                    try
                    {
                        using var doc = JsonDocument.Parse(argsJson);
                        result.Add(new ToolCall
                        {
                            Id = Guid.NewGuid().ToString("N"),
                            Type = "function",
                            Function = new FunctionCall { Name = name, Arguments = argsJson }
                        });
                        spans.Add((nameIdx, braceEnd.Value - nameIdx + 1));
                        LogAgent($"Parsed inline tool call: {name}({argsJson.Length} chars)");
                        searchFrom = braceEnd.Value + 1;
                    }
                    catch (JsonException)
                    {
                        searchFrom = nameIdx + name.Length;
                    }
                }
            }

            if (result.Count == 0) return (null, content);

            // Remove spans in reverse order to keep indices valid
            var cleaned = content;
            foreach (var span in spans.OrderByDescending(s => s.Start))
                cleaned = cleaned.Remove(span.Start, span.Length);

            return (result, cleaned.Trim());
        }

        /// <summary>Find the index of the closing '}' that matches the opening '{' at position start, respecting JSON strings.</summary>
        private static int? FindMatchingBrace(string text, int start)
        {
            if (start >= text.Length || text[start] != '{') return null;
            int depth = 0;
            bool inString = false;
            for (int i = start; i < text.Length; i++)
            {
                char c = text[i];
                if (inString)
                {
                    if (c == '\\') { i++; continue; } // skip escaped char
                    if (c == '"') inString = false;
                    continue;
                }
                switch (c)
                {
                    case '"': inString = true; break;
                    case '{': depth++; break;
                    case '}':
                        depth--;
                        if (depth == 0) return i;
                        break;
                }
            }
            return null; // unbalanced
        }
        
        /// <summary>
        /// Gets the configured max iterations from AgentConfig, falling back to default.
        /// </summary>
        private static int GetMaxIterations() => AgentConfig.Config.MaxIterations > 0 
            ? AgentConfig.Config.MaxIterations 
            : DefaultMaxIterations;
        
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
            Action<string, string, string, TimeSpan>? onToolComplete = null,  // name, argsJson, result, elapsed
            ToolProgressCallback? onToolProgress = null,
            Action<string, string>? onToolCall = null,
            int? maxIterations = null,
            Action<int, int>? onIteration = null)  // current, max
        {
            // Set current messages in context for tools that need it (e.g., vision analysis)
            AgentContext.SetCurrentMessages(messages);
            
            int maxIter = maxIterations ?? GetMaxIterations();
            int iteration = 0;
            bool anyToolCalled = false;
            var failureTracker = new Dictionary<string, int>(); // Track consecutive failures per tool
            
            while (true)
            {
                iteration++;
                if (iteration > maxIter)
                {
                    LogAgent($"Loop limit reached ({maxIter} iterations). Stopping.");
                    return $"[Agent stopped: Maximum iteration limit ({maxIter}) reached. The task may be too complex or the model is stuck in a loop.]";
                }
                
                onIteration?.Invoke(iteration, maxIter);
                
                // DeepSeek-reasoner: clear reasoning_content from older turns when a new user turn starts.
                // During tool call loops (last msg is tool), keep current turn's reasoning_content intact.
                var lastMsg = messages.Count > 0 ? messages[^1] : null;
                if (lastMsg?.Role == "user")
                {
                    foreach (var m in messages)
                        m.ReasoningContent = null;
                }
                
                var req = new ChatRequest
                {
                    Model = model,
                    Messages = messages,
                    Tools = tools,
                    ToolChoice = "auto",
                    Temperature = 0.2,
                    Max_Tokens = AgentConfig.Config.MaxOutputTokens > 0 ? AgentConfig.Config.MaxOutputTokens : null
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
                        // Use configurable path (no leading /) so it appends to BaseAddress path correctly
                        using var resp = await http.PostAsJsonAsync(AgentConfig.GetChatCompletionsPath(model), req, JsonOpts, token);
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
                    
                    // If API returned context length info, update the model config
                    if (u.MaxContextLength.HasValue && u.MaxContextLength.Value > 0)
                    {
                        var modelEndpoint = ModelRegistry.Instance.GetModel(model);
                        if (modelEndpoint != null && modelEndpoint.MaxContextLength != u.MaxContextLength.Value)
                        {
                            modelEndpoint.MaxContextLength = u.MaxContextLength.Value;
                            LogAgent($"Updated {model} max context length to {u.MaxContextLength.Value:N0} from API response");
                        }
                    }
                    
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

                if (msg.ToolCalls == null || msg.ToolCalls.Count == 0)
                {
                    // Try to parse inline tool calls from content text (e.g. read_file{"path":"..."})
                    var (inlineCalls, cleanedContent) = TryParseInlineToolCalls(msg.Content, tools);
                    if (inlineCalls != null)
                    {
                        LogAgent($"Recovered {inlineCalls.Count} inline tool call(s) from content text");
                        msg.ToolCalls = inlineCalls;
                        msg.Content = cleanedContent;
                    }
                }

                // Check if the model indicated it wants to continue but didn't make a tool call
                // We look for action phrases, especially those ending with ":" which strongly indicate intent
                if (msg.ToolCalls == null || msg.ToolCalls.Count == 0)
                {
                    bool wantsToContinue = false;
                    
                    if (msg.Content != null)
                    {
                        // Strong signal: phrases ending with colon (e.g., "Let me check the screen:", "Now I will press C:")
                        var colonPattern = @"(?:let me|I will|I'll|now I|next,? I|let's|I need to|I should|I'm going to)[^.!?\n]*:";
                        if (System.Text.RegularExpressions.Regex.IsMatch(msg.Content, colonPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        {
                            wantsToContinue = true;
                            LogAgent("Detected action phrase with colon - strong intent signal");
                        }
                        // Weaker signal: just the action phrases without colon
                        else if (msg.Content.Contains("let me ", StringComparison.OrdinalIgnoreCase) ||
                                 msg.Content.Contains("I will ", StringComparison.OrdinalIgnoreCase) ||
                                 msg.Content.Contains("I'll ", StringComparison.OrdinalIgnoreCase) ||
                                 msg.Content.Contains("Now I ", StringComparison.OrdinalIgnoreCase) ||
                                 msg.Content.Contains("Next, I", StringComparison.OrdinalIgnoreCase) ||
                                 msg.Content.Contains("Let's ", StringComparison.OrdinalIgnoreCase) ||
                                 msg.Content.Contains("I need to ", StringComparison.OrdinalIgnoreCase) ||
                                 msg.Content.Contains("I should ", StringComparison.OrdinalIgnoreCase) ||
                                 msg.Content.Contains("I'm going to ", StringComparison.OrdinalIgnoreCase))
                        {
                            // Only trigger on weak signals if the message is short and doesn't end with punctuation
                            var trimmed = msg.Content.TrimEnd();
                            if (trimmed.Length < 500 && !trimmed.EndsWith(".") && !trimmed.EndsWith("!") && !trimmed.EndsWith("?"))
                            {
                                wantsToContinue = true;
                                LogAgent("Detected action phrase with incomplete ending");
                            }
                        }
                    }
                    
                    if (wantsToContinue)
                    {
                        LogAgent("Model indicated intent to continue but made no tool call. Prompting to proceed.");
                        
                        // Add the assistant message
                        messages.Add(new ChatMessage
                        {
                            Role = "assistant",
                            Content = msg.Content,
                            ReasoningContent = msg.ReasoningContent
                        });
                        
                        // Add a prompt to continue
                        messages.Add(new ChatMessage
                        {
                            Role = "user",
                            Content = "Please proceed with the action you described. Make the appropriate tool call."
                        });
                        
                        continue;
                    }
                }

                if (msg.ToolCalls is { Count: > 0 })
                {
                    messages.Add(msg);

                    foreach (var call in msg.ToolCalls)
                    {
                        var name = call.Function.Name;
                        var argsJson = call.Function.Arguments ?? "{}";
                        onToolCall?.Invoke(name, argsJson);
                        var toolStart = DateTime.Now;
                        var toolResult = await ToolExecutor.ExecuteToolAsync(name, argsJson, ct, onToolProgress);
                        var toolElapsed = DateTime.Now - toolStart;
                        ConsoleHelpers.PrintToolCall(name, argsJson, toolResult);
                        onToolResult?.Invoke(name, toolResult);
                        onToolComplete?.Invoke(name, argsJson, toolResult, toolElapsed);
                        anyToolCalled = true;
                        
                        // Track failures for loop detection
                        // Check for actual error values, not "error":null which indicates success
                        bool isFailure = toolResult.Contains("\"success\":false") ||
                                        toolResult.Contains("\"timed_out\":true") ||
                                        toolResult.Contains("\"stderr\":\"timeout\"") ||
                                        (toolResult.Contains("\"error\":") && 
                                         !toolResult.Contains("\"error\":null") &&
                                         !toolResult.Contains("\"error\": null"));
                        if (isFailure)
                        {
                            failureTracker[name] = failureTracker.GetValueOrDefault(name, 0) + 1;
                            if (failureTracker[name] >= MaxConsecutiveFailures)
                            {
                                LogAgent($"Tool {name} failed {MaxConsecutiveFailures} times consecutively. Stopping.");
                                
                                // Add the tool result to keep conversation state valid
                                messages.Add(new ChatMessage(
                                    role: "tool",
                                    content: CompressToolResult(name, toolResult),
                                    name: name,
                                    toolCallId: call.Id
                                ));
                                
                                return $"[Agent stopped: Tool '{name}' failed {MaxConsecutiveFailures} times consecutively. Please check the tool configuration or try a different approach.]";
                            }
                        }
                        else
                        {
                            failureTracker[name] = 0; // Reset on success
                        }

                        messages.Add(new ChatMessage(
                            role: "tool",
                            content: CompressToolResult(name, toolResult),
                            name: name,
                            toolCallId: call.Id
                        ));
                        InjectVisionImageIfApplicable(name, toolResult, model, messages);
                    }

                    continue;
                }

                // For thinking models: if content is empty but reasoning was produced, use reasoning as fallback
                if (string.IsNullOrEmpty(msg.Content) && !string.IsNullOrEmpty(msg.ReasoningContent))
                {
                    LogAgent("Content empty but reasoning available — using reasoning as fallback content");
                    return msg.ReasoningContent;
                }

                // Guard against silent stops: always return something meaningful.
                if (string.IsNullOrEmpty(msg.Content))
                {
                    var fallback = anyToolCalled
                        ? "✅ Done."
                        : "⚠️ The model returned an empty response.";
                    LogAgent($"Empty content at loop exit — returning fallback: {fallback}");
                    return fallback;
                }
                
                return msg.Content;
            }
        }

        /// <summary>
        /// Like CompleteWithToolsAsync, but streams tokens for final answers.
        /// Prints tokens as they arrive via onToken (e.g., Console.Write).
        /// For thinking models, reasoning tokens are sent to onReasoningToken.
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
            Action<string, string, string, TimeSpan>? onToolComplete = null,  // name, argsJson, result, elapsed
            ToolProgressCallback? onToolProgress = null,
            Action<string, string>? onToolCall = null,
            int? maxIterations = null,
            Action<string>? onReasoningToken = null,
            Action<string>? onContentReplace = null,
            Action<int, int>? onIteration = null)  // current, max
        {
            // Set current messages in context for tools that need it (e.g., vision analysis)
            AgentContext.SetCurrentMessages(messages);
            
            int maxIter = maxIterations ?? GetMaxIterations();
            int iteration = 0;
            bool anyToolCalled = false;
            var failureTracker = new Dictionary<string, int>();
            var executedToolCalls = new HashSet<string>(); // Track tool call signatures to detect loops
            int noProgressCount = 0;
            const int MaxNoProgress = 5;   // warn at 3, hard-stop at 5
            const int WarnNoProgress = 3;
            
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
                
                onIteration?.Invoke(iteration, maxIter);
                
                // DeepSeek-reasoner: clear reasoning_content from older turns when a new user turn starts.
                // During tool call loops (last msg is tool), keep current turn's reasoning_content intact.
                var lastMsg = messages.Count > 0 ? messages[^1] : null;
                if (lastMsg?.Role == "user")
                {
                    foreach (var m in messages)
                        m.ReasoningContent = null;
                }
                
                var req = new ChatRequest
                {
                    Model = model,
                    Messages = messages,
                    Tools = tools,
                    ToolChoice = "auto",
                    Temperature = 0.2,
                    Max_Tokens = AgentConfig.Config.MaxOutputTokens > 0 ? AgentConfig.Config.MaxOutputTokens : null
                };
                
                // Log message summary for debugging
                LogAgent($"Sending {messages.Count} messages to API (iteration {iteration}):");
                foreach (var m in messages)
                {
                    var preview = m.Content?.Length > 100 ? m.Content.Substring(0, 100) + "..." : m.Content ?? "(no content)";
                    LogAgent($"  [{m.Role}]: {preview}");
                }

                LogAgent($"Calling StreamChatOnceAsync... (iteration {iteration})");
                var result = await StreamResult.StreamChatOnceAsync(http, req, ct, onToken, onUsage, onReasoningToken);
                LogAgent($"StreamChatOnceAsync returned, ToolCalls={result.ToolCalls?.Count ?? 0}, Content length={result.Content?.Length ?? 0}, Reasoning length={result.ReasoningContent?.Length ?? 0}");

                // Update token tracker from streaming usage (the onUsage callback doesn't do this)
                var tracker = Models.AgentContext.GetEffectiveTokenTracker();
                if (result.Usage != null)
                {
                    tracker.UpdateFromUsage(result.Usage);
                }

                // If API returned context length info, update the model config
                if (result.Usage?.MaxContextLength.HasValue == true && result.Usage.MaxContextLength.Value > 0)
                {
                    var modelEndpoint = ModelRegistry.Instance.GetModel(model);
                    if (modelEndpoint != null && modelEndpoint.MaxContextLength != result.Usage.MaxContextLength.Value)
                    {
                        modelEndpoint.MaxContextLength = result.Usage.MaxContextLength.Value;
                        LogAgent($"Updated {model} max context length to {result.Usage.MaxContextLength.Value:N0} from API response");
                    }
                }

                // Check context size and auto-summarize if needed
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
                
                // Check if the model indicated it wants to continue but didn't make a tool call
                // This handles cases where the model says "Now let me..." but forgets to call the tool
                // We look for action phrases, especially those ending with ":" which strongly indicate intent
                if (result.ToolCalls == null || result.ToolCalls.Count == 0)
                {
                    // Try to parse inline tool calls from content text (e.g. read_file{"path":"..."})
                    var (inlineCalls, cleanedContent) = TryParseInlineToolCalls(result.Content, tools);
                    if (inlineCalls != null)
                    {
                        LogAgent($"Recovered {inlineCalls.Count} inline tool call(s) from streaming content text");
                        // Update UI to remove the raw tool text that was already streamed
                        onContentReplace?.Invoke(cleanedContent ?? "");
                        result = new StreamResult
                        {
                            Content = cleanedContent,
                            ReasoningContent = result.ReasoningContent,
                            ToolCalls = inlineCalls,
                            FinishReason = result.FinishReason,
                            Usage = result.Usage
                        };
                    }
                }

                if (result.ToolCalls == null || result.ToolCalls.Count == 0)
                {
                    bool wantsToContinue = false;
                    
                    if (result.Content != null)
                    {
                        // Strong signal: phrases ending with colon (e.g., "Let me check the screen:", "Now I will press C:")
                        // This regex looks for common action phrases followed by a colon
                        var colonPattern = @"(?:let me|I will|I'll|now I|next,? I|let's|I need to|I should|I'm going to)[^.!?\n]*:";
                        if (System.Text.RegularExpressions.Regex.IsMatch(result.Content, colonPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        {
                            wantsToContinue = true;
                            LogAgent("Detected action phrase with colon - strong intent signal");
                        }
                        // Weaker signal: just the action phrases without colon (may have false positives)
                        else if (result.Content.Contains("let me ", StringComparison.OrdinalIgnoreCase) ||
                                 result.Content.Contains("I will ", StringComparison.OrdinalIgnoreCase) ||
                                 result.Content.Contains("I'll ", StringComparison.OrdinalIgnoreCase) ||
                                 result.Content.Contains("Now I ", StringComparison.OrdinalIgnoreCase) ||
                                 result.Content.Contains("Next, I", StringComparison.OrdinalIgnoreCase) ||
                                 result.Content.Contains("Let's ", StringComparison.OrdinalIgnoreCase) ||
                                 result.Content.Contains("I need to ", StringComparison.OrdinalIgnoreCase) ||
                                 result.Content.Contains("I should ", StringComparison.OrdinalIgnoreCase) ||
                                 result.Content.Contains("I'm going to ", StringComparison.OrdinalIgnoreCase))
                        {
                            // Only trigger on weak signals if the message is short (less likely to be a summary)
                            // and doesn't end with a period (indicating incomplete thought)
                            var trimmed = result.Content.TrimEnd();
                            if (trimmed.Length < 500 && !trimmed.EndsWith(".") && !trimmed.EndsWith("!") && !trimmed.EndsWith("?"))
                            {
                                wantsToContinue = true;
                                LogAgent("Detected action phrase with incomplete ending");
                            }
                        }
                    }
                    
                    if (wantsToContinue)
                    {
                        LogAgent("Model indicated intent to continue but made no tool call. Prompting to proceed.");
                        
                        // Add the assistant message
                        messages.Add(new ChatMessage
                        {
                            Role = "assistant",
                            Content = result.Content,
                            ReasoningContent = result.ReasoningContent
                        });
                        
                        // Add a prompt to continue
                        messages.Add(new ChatMessage
                        {
                            Role = "user",
                            Content = "Please proceed with the action you described. Make the appropriate tool call."
                        });
                        
                        continue;
                    }
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
                        if (noProgressCount >= WarnNoProgress)
                        {
                            // Inject a nudge instead of stopping — give the LLM a chance to recover
                            messages.Add(new ChatMessage("user",
                                "[SYSTEM WARNING] You are repeating the same tool calls that already failed. " +
                                "You MUST try a completely different approach. Do NOT call the same tool with the same arguments again. " +
                                "If you cannot complete the task, explain what is blocking you and stop."));
                            LogAgent("Injected loop warning to model");
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
                        ReasoningContent = result.ReasoningContent, // Required by DeepSeek-reasoner during tool call loops
                        ToolCalls = result.ToolCalls
                    });

                    foreach (var call in result.ToolCalls)
                    {
                        var name = call.Function.Name;
                        var argsJson = call.Function.Arguments ?? "{}";
                        var callSignature = $"{name}:{argsJson}";
                        executedToolCalls.Add(callSignature);
                        anyToolCalled = true;
                        
                        LogAgent($"Executing tool: {name}");
                        onToolCall?.Invoke(name, argsJson);
                        var toolStart = DateTime.Now;
                        var toolResult = await ToolExecutor.ExecuteToolAsync(name, argsJson, ct, onToolProgress);
                        var toolElapsed = DateTime.Now - toolStart;
                        LogAgent($"Tool {name} completed in {toolElapsed.TotalSeconds:F1}s, result length={toolResult.Length}");
                        ConsoleHelpers.PrintToolCall(name, argsJson, toolResult);
                        onToolResult?.Invoke(name, toolResult);
                        onToolComplete?.Invoke(name, argsJson, toolResult, toolElapsed);
                        
                        // Track failures for loop detection
                        // Check for actual error values, not "error":null which indicates success
                        bool isFailure = toolResult.Contains("\"success\":false") ||
                                        toolResult.Contains("\"timed_out\":true") ||
                                        toolResult.Contains("\"stderr\":\"timeout\"") ||
                                        (toolResult.Contains("\"error\":") && 
                                         !toolResult.Contains("\"error\":null") &&
                                         !toolResult.Contains("\"error\": null"));
                        if (isFailure)
                        {
                            failureTracker[name] = failureTracker.GetValueOrDefault(name, 0) + 1;
                            if (failureTracker[name] >= MaxConsecutiveFailures)
                            {
                                LogAgent($"Tool {name} failed {MaxConsecutiveFailures} times consecutively. Stopping.");
                                
                                // Add the tool result to keep conversation state valid
                                messages.Add(new ChatMessage(
                                    role: "tool",
                                    content: CompressToolResult(name, toolResult),
                                    name: name,
                                    toolCallId: call.Id
                                ));
                                
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
                            content: CompressToolResult(name, toolResult),
                            name: name,
                            toolCallId: call.Id
                        ));
                        InjectVisionImageIfApplicable(name, toolResult, model, messages);
                    }

                    LogAgent("Continuing loop for next LLM response...");
                    continue;
                }

                LogAgent($"Returning final content, length={result.Content?.Length ?? 0}, reasoning={result.ReasoningContent?.Length ?? 0}");
                
                // For thinking models: if content is empty but reasoning was produced,
                // use reasoning as fallback (model exhausted output budget on thinking)
                if (string.IsNullOrEmpty(result.Content) && !string.IsNullOrEmpty(result.ReasoningContent))
                {
                    LogAgent("Content empty but reasoning available — using reasoning as fallback content");
                    return result.ReasoningContent;
                }

                // Guard against silent stops: always surface something to the user.
                if (string.IsNullOrEmpty(result.Content))
                {
                    var fallback = anyToolCalled
                        ? "✅ Done."
                        : "⚠️ The model returned an empty response.";
                    LogAgent($"Empty content at loop exit — emitting fallback: {fallback}");
                    onToken?.Invoke(fallback);
                    return fallback;
                }
                
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
        /// Returns (success, summaryContent) tuple.
        /// </summary>
        public static async Task<(bool Success, string? SummaryContent)> SummarizeConversationAsync(
            HttpClient http,
            string model,
            List<ChatMessage> messages,
            CancellationToken ct,
            Action<string>? onStatus = null)
        {
            if (messages.Count < 3) // Need at least system + some conversation
            {
                LogAgent("Not enough messages to summarize");
                return (false, null);
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

                using var resp = await http.PostAsJsonAsync(AgentConfig.GetChatCompletionsPath(model), summaryRequest, JsonOpts, ct);
                resp.EnsureSuccessStatusCode();
                var body = await resp.Content.ReadFromJsonAsync<ChatResponse>(JsonOpts, ct);
                
                var summary = body?.Choices?[0]?.Message?.Content;
                if (string.IsNullOrEmpty(summary))
                {
                    LogAgent("Summarization returned empty result");
                    return (false, null);
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
                messages.Add(ChatMessage.CreateAssistant("I understand. I have the context from the summarized conversation. I'll continue from where we left off."));

                LogAgent($"Conversation summarized. New message count: {messages.Count}");
                onStatus?.Invoke($"Conversation summarized. Reduced from many messages to {messages.Count}.");
                
                return (true, summary);
            }
            catch (Exception ex)
            {
                LogAgent($"Summarization failed: {ex.Message}");
                onStatus?.Invoke($"Summarization failed: {ex.Message}");
                return (false, null);
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
            messages.Add(ChatMessage.CreateAssistant("Understood. I'll continue working on the current task."));
            
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
            Action<string>? onStatus = null,
            Func<string, Task>? onSummarized = null)
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
            var (summarized, summaryContent) = await SummarizeConversationAsync(http, model, messages, ct, onStatus);
            
            // If summarization succeeded and we have a callback, record it
            if (summarized && !string.IsNullOrEmpty(summaryContent) && onSummarized != null)
            {
                try
                {
                    await onSummarized(summaryContent);
                }
                catch (Exception ex)
                {
                    LogAgent($"Failed to record summarization: {ex.Message}");
                }
            }
            
            // If still over truncation threshold, truncate
            if (tracker.UsagePercent >= TruncationThreshold)
            {
                LogAgent($"Still at {tracker.UsagePercent:P0} after summarization, truncating");
                TruncateConversation(messages, keepRecentCount: 4, onStatus);
            }

            return summarized || tracker.UsagePercent >= TruncationThreshold;
        }

        /// <summary>
        /// If the tool result contains base64 image data and the active model supports vision,
        /// inject a multimodal user message so the LLM can "see" the image directly.
        /// </summary>
        private static void InjectVisionImageIfApplicable(string toolName, string toolResult, string model, List<ChatMessage> messages)
        {
            if (toolName != "ui_capture") return;
            
            var modelEndpoint = ModelRegistry.Instance.GetModel(model);
            if (modelEndpoint == null || !modelEndpoint.SupportsVision) return;
            
            try
            {
                using var doc = JsonDocument.Parse(toolResult);
                var root = doc.RootElement;
                if (!root.TryGetProperty("base64_data", out var b64Prop)) return;
                if (!root.TryGetProperty("success", out var successProp) || !successProp.GetBoolean()) return;
                
                var base64 = b64Prop.GetString();
                if (string.IsNullOrEmpty(base64)) return;
                
                var mime = root.TryGetProperty("mime_type", out var mimeProp) 
                    ? mimeProp.GetString() ?? "image/png" 
                    : "image/png";
                
                // Inject a user message with the screenshot so the vision model can see it
                messages.Add(ChatMessage.CreateWithImage("user", 
                    "[Screenshot captured — see image below]", base64, mime));
                LogAgent($"Injected screenshot image into conversation for vision model ({base64.Length / 1024}KB base64)");
            }
            catch { /* not parseable, skip */ }
        }

        /// <summary>
        /// Maximum characters for tool results in context (to prevent token bloat)
        /// </summary>
        private const int MaxToolResultLength = 8000;
        
        /// <summary>
        /// Compress/truncate tool results to reduce token usage.
        /// Smart compression based on tool type and content.
        /// Also used during session restore to re-apply the same size limits.
        /// </summary>
        public static string CompressToolResult(string toolName, string result)
        {
            if (string.IsNullOrEmpty(result))
                return result;
                
            // Already small enough
            if (result.Length <= MaxToolResultLength)
                return result;
            
            LogAgent($"Compressing {toolName} result from {result.Length} chars");
            
            // Try to parse as JSON to handle structured results
            try
            {
                using var doc = JsonDocument.Parse(result);
                var root = doc.RootElement;
                
                // For search_files with many matches, truncate the matches array
                if (toolName == "search_files" && root.TryGetProperty("matches", out var matches))
                {
                    if (matches.ValueKind == JsonValueKind.Array)
                    {
                        var matchCount = matches.GetArrayLength();
                        if (matchCount > 50)
                        {
                            // Take first 50 matches and add summary
                            var truncated = new List<string>();
                            int i = 0;
                            foreach (var m in matches.EnumerateArray())
                            {
                                if (i >= 50) break;
                                truncated.Add(m.GetString() ?? "");
                                i++;
                            }
                            return JsonSerializer.Serialize(new 
                            { 
                                matches = truncated,
                                truncated = true,
                                total_matches = matchCount,
                                showing = 50
                            });
                        }
                    }
                }
                
                // For read_file, truncate content but keep metadata
                if (toolName == "read_file" && root.TryGetProperty("content", out var content))
                {
                    var contentStr = content.GetString() ?? "";
                    if (contentStr.Length > MaxToolResultLength - 500)
                    {
                        var truncatedContent = contentStr.Substring(0, MaxToolResultLength - 500);
                        // Find last newline to avoid cutting mid-line
                        var lastNewline = truncatedContent.LastIndexOf('\n');
                        if (lastNewline > MaxToolResultLength / 2)
                            truncatedContent = truncatedContent.Substring(0, lastNewline);
                        
                        return JsonSerializer.Serialize(new 
                        { 
                            content = truncatedContent,
                            sha256 = root.TryGetProperty("sha256", out var sha) ? sha.GetString() : null,
                            truncated = true,
                            original_length = contentStr.Length,
                            showing_chars = truncatedContent.Length
                        });
                    }
                }
                
                // For build/test output with stdout/stderr, compress those
                if (toolName.StartsWith("dotnet_") || toolName == "run_process")
                {
                    root.TryGetProperty("stdout", out var stdout);
                    root.TryGetProperty("stderr", out var stderr);
                    
                    var stdoutStr = stdout.ValueKind == JsonValueKind.String ? stdout.GetString() ?? "" : "";
                    var stderrStr = stderr.ValueKind == JsonValueKind.String ? stderr.GetString() ?? "" : "";
                    
                    // Keep errors/warnings, truncate verbose output
                    if (stdoutStr.Length + stderrStr.Length > MaxToolResultLength - 200)
                    {
                        // Extract important lines (errors, warnings, test results)
                        var importantPatterns = new[] { "error", "warning", "fail", "pass", "succeed", "Error:", "FAIL:", "PASS:" };
                        var importantLines = new List<string>();
                        
                        foreach (var line in (stdoutStr + "\n" + stderrStr).Split('\n'))
                        {
                            foreach (var pattern in importantPatterns)
                            {
                                if (line.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                                {
                                    importantLines.Add(line.Trim());
                                    break;
                                }
                            }
                        }
                        
                        var compressed = string.Join("\n", importantLines.Take(100));
                        if (compressed.Length < 100)
                        {
                            // If no important lines found, just truncate
                            compressed = (stdoutStr + "\n" + stderrStr);
                            if (compressed.Length > MaxToolResultLength - 200)
                                compressed = compressed.Substring(0, MaxToolResultLength - 200);
                        }
                        
                        return JsonSerializer.Serialize(new 
                        { 
                            output = compressed,
                            exit_code = root.TryGetProperty("exit_code", out var ec) ? ec.GetInt32() : 0,
                            truncated = true,
                            original_length = stdoutStr.Length + stderrStr.Length
                        });
                    }
                }
            }
            catch
            {
                // Not JSON or parse error, fall through to simple truncation
            }
            
            // Default: simple truncation with note
            var truncLen = MaxToolResultLength - 100;
            return result.Substring(0, truncLen) + $"\n\n[... truncated, original {result.Length} chars]";
        }

        /// <summary>
        /// Get context length from LM Studio API
        /// </summary>
        public static async Task<int?> GetContextLengthAsync(HttpClient http, string modelId, CancellationToken ct)
        {
            // Try LM Studio proprietary API first
            try
            {
                using var resp = await http.GetAsync($"/api/v0/models/{Uri.EscapeDataString(modelId)}", ct);
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                    var root = doc.RootElement;

                    if (root.TryGetProperty("max_context_length", out var m) && m.ValueKind == JsonValueKind.Number)
                        return m.GetInt32();

                    if (root.TryGetProperty("model_info", out var mi) &&
                        mi.ValueKind == JsonValueKind.Object &&
                        mi.TryGetProperty("context_length", out var cl) &&
                        cl.ValueKind == JsonValueKind.Number)
                        return cl.GetInt32();
                }
            }
            catch { }

            // Try standard OpenAI-compatible /v1/models endpoint
            try
            {
                using var resp = await http.GetAsync("/v1/models", ct);
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                    if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var model in data.EnumerateArray())
                        {
                            var id = model.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                            if (id == null || !id.Equals(modelId, StringComparison.OrdinalIgnoreCase))
                                continue;

                            // Common context length field names across providers
                            foreach (var fieldName in new[] { "context_length", "max_context_length", "context_window", "max_model_len" })
                            {
                                if (model.TryGetProperty(fieldName, out var val) && val.ValueKind == JsonValueKind.Number)
                                {
                                    var length = val.GetInt32();
                                    if (length > 0) return length;
                                }
                            }

                            // Nested under "meta" or "model_info"
                            foreach (var nested in new[] { "meta", "model_info" })
                            {
                                if (model.TryGetProperty(nested, out var nestedObj) && nestedObj.ValueKind == JsonValueKind.Object)
                                {
                                    foreach (var fieldName in new[] { "context_length", "max_context_length", "context_window", "max_model_len" })
                                    {
                                        if (nestedObj.TryGetProperty(fieldName, out var val) && val.ValueKind == JsonValueKind.Number)
                                        {
                                            var length = val.GetInt32();
                                            if (length > 0) return length;
                                        }
                                    }
                                }
                            }

                            break;
                        }
                    }
                }
            }
            catch { }

            // Fall back to known model context lengths for providers that don't expose this via API
            var knownLength = GetKnownContextLength(modelId);
            if (knownLength.HasValue)
            {
                LogAgent($"Using known context length {knownLength.Value:N0} for {modelId}");
                return knownLength;
            }

            return null;
        }

        /// <summary>
        /// Known context lengths for popular models whose APIs don't report context size.
        /// </summary>
        private static int? GetKnownContextLength(string modelId)
        {
            var id = modelId.ToLowerInvariant();

            // DeepSeek models (128K context)
            if (id.Contains("deepseek"))
                return 131072; // 128K

            // OpenAI models
            if (id.StartsWith("gpt-4o")) return 128000;
            if (id.StartsWith("gpt-4-turbo")) return 128000;
            if (id.StartsWith("gpt-4-1")) return 1047576; // 1M
            if (id.StartsWith("gpt-4")) return 8192;
            if (id.StartsWith("gpt-3.5-turbo")) return 16385;
            if (id.StartsWith("o1")) return 200000;
            if (id.StartsWith("o3")) return 200000;
            if (id.StartsWith("o4")) return 200000;

            // Anthropic models
            if (id.Contains("claude-3") || id.Contains("claude-sonnet") || id.Contains("claude-opus") || id.Contains("claude-haiku"))
                return 200000;

            // Google Gemini
            if (id.Contains("gemini-2")) return 1048576;
            if (id.Contains("gemini-1.5")) return 1048576;
            if (id.Contains("gemini")) return 32768;

            // Qwen models
            if (id.Contains("qwen3")) return 131072;
            if (id.Contains("qwen2.5") && id.Contains("coder")) return 131072;
            if (id.Contains("qwen2.5")) return 131072;
            if (id.Contains("qwen")) return 32768;

            // Llama models
            if (id.Contains("llama-3.3") || id.Contains("llama-3.1")) return 131072;
            if (id.Contains("llama-3")) return 8192;
            if (id.Contains("llama")) return 4096;

            // Mistral models
            if (id.Contains("mistral-large")) return 131072;
            if (id.Contains("mistral")) return 32768;

            return null;
        }
    }
}
