using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using thuvu.Models;
using System.Buffers;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace thuvu
{


    // Result for a single streamed assistant turn
    public class StreamResult
    {
        public string Content { get; init; } = "";
        /// <summary>
        /// Reasoning/thinking content from thinking models (e.g., GLM 4.7, DeepSeek-R1)
        /// </summary>
        public string? ReasoningContent { get; init; }
        public List<ToolCall>? ToolCalls { get; init; }
        public string? FinishReason { get; init; }
        public Usage? Usage { get; init; } // Optional, if stream_options.include_usage is set


        /// <summary>
        /// Streams a single assistant turn. If the model emits tool calls,
        /// they are accumulated and returned in ToolCalls; Content may be empty in that case.
        /// If it emits plain text, tokens are sent to onToken as they arrive.
        /// For thinking models, reasoning_content is sent to onReasoningToken.
        /// </summary>
        public static async Task<StreamResult> StreamChatOnceAsync(
            HttpClient http,
            ChatRequest req,
            CancellationToken ct,
            Action<string>? onToken = null,
            Action<Usage>? onUsage = null,
            Action<string>? onReasoningToken = null)
        {
            // Check if current model supports vision - if not, serialize multimodal messages as text-only
            var modelConfig = Models.ModelRegistry.Instance?.GetModel(req.Model);
            var supportsVision = modelConfig?.SupportsVision ?? false;
            
            // Prepare messages for serialization
            // For non-vision models, multimodal messages are converted to text-only during serialization
            // The original messages list is NOT modified - only the serialized output changes
            object[] serializedMessages;
            if (supportsVision)
            {
                // Vision model: serialize messages as-is (ChatMessageConverter handles multimodal)
                serializedMessages = req.Messages.Cast<object>().ToArray();
            }
            else
            {
                // Non-vision model: convert multimodal messages to text-only for serialization
                serializedMessages = req.Messages.Select(m => 
                {
                    if (m.IsMultimodal)
                    {
                        // Extract just the text content, add note about image
                        var textContent = m.TextContent ?? "";
                        var hasImage = m.ContentParts?.Any(p => p.Type == "image_url") ?? false;
                        if (hasImage)
                        {
                            textContent = $"[An image was shared here]\n{textContent}";
                        }
                        return (object)new { role = m.Role, content = textContent };
                    }
                    return (object)m; // Regular messages serialize normally
                }).ToArray();
            }
            
            // Build a streaming request
            var streamingReq = new
            {
                model = req.Model,
                messages = serializedMessages,
                tools = req.Tools,
                tool_choice = req.ToolChoice,
                temperature = req.Temperature,
                stream = true,
                stream_options = new { include_usage = true }
            };
            
            // Log tool names being sent
            if (req.Tools != null)
            {
                var toolNames = req.Tools.Select(t => t.Function?.Name ?? "?").ToList();
                Console.WriteLine($"[StreamResult] Sending {toolNames.Count} tools: {string.Join(", ", toolNames)}");
            }

            void LogStream(string msg) 
            {
                var logLine = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
                System.Diagnostics.Debug.WriteLine(logLine);
                Console.WriteLine($"[StreamResult] {msg}");
                try { File.AppendAllText("stream_debug.log", logLine + Environment.NewLine); } catch { }
            }
            
            LogStream($"Sending HTTP POST request to BaseAddress={http.BaseAddress}, Model={streamingReq.model}");
            LogStream($"HttpClient HasAuth={http.DefaultRequestHeaders.Authorization != null}");
            
            using var jsonContent = new StringContent(JsonSerializer.Serialize(streamingReq, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }), Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync(AgentConfig.GetChatCompletionsPath(streamingReq.model), jsonContent, ct);
            
            LogStream($"HTTP response received, status={resp.StatusCode}");
            LogStream($"Response ContentType={resp.Content.Headers.ContentType}");
            
            if (!resp.IsSuccessStatusCode)
            {
                var errorBody = await resp.Content.ReadAsStringAsync(ct);
                LogStream($"HTTP error body: {errorBody}");
                resp.EnsureSuccessStatusCode(); // This will throw with the status code
            }

            // For debugging: read raw response first to see what we're getting
            var rawResponse = await resp.Content.ReadAsStringAsync(ct);
            LogStream($"Raw response length={rawResponse.Length}, first 500 chars: {rawResponse.Substring(0, Math.Min(500, rawResponse.Length))}");
            
            // If empty or HTML, log and handle
            if (string.IsNullOrEmpty(rawResponse))
            {
                LogStream("ERROR: Empty response from API");
                throw new InvalidOperationException("API returned empty response");
            }
            
            if (rawResponse.TrimStart().StartsWith("<"))
            {
                LogStream($"ERROR: API returned HTML instead of JSON/SSE: {rawResponse.Substring(0, Math.Min(200, rawResponse.Length))}");
                throw new InvalidOperationException($"API returned HTML instead of JSON. Response: {rawResponse.Substring(0, Math.Min(500, rawResponse.Length))}");
            }

            LogStream("Processing response as SSE stream...");
            using var reader = new StreamReader(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(rawResponse)));
            LogStream("Stream reader created");

            var sbContent = new StringBuilder();
            var sbReasoning = new StringBuilder(); // For thinking model reasoning_content
            var finishReason = (string?)null;

            // Collect tool_calls deltas by index, merging arguments chunks
            var toolBuilders = new Dictionary<int, (string? Id, string? Name, StringBuilder Args)>();

            string? line;
            Usage? usage = null;
            
            LogStream("Starting stream read loop");
            
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                
                // Read with a 5-second idle timeout using Task.WhenAny
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                
                try
                {
                    var readTask = reader.ReadLineAsync();
                    var delayTask = Task.Delay(Timeout.Infinite, linkedCts.Token);
                    
                    var completed = await Task.WhenAny(readTask, delayTask);
                    
                    if (completed == delayTask)
                    {
                        // Check if it was user cancellation or timeout
                        if (ct.IsCancellationRequested)
                            throw new OperationCanceledException(ct);
                        
                        // Timeout - if we've received content, we're done
                        LogStream($"Timeout after 5s, content length={sbContent.Length}, finishReason={finishReason}");
                        if (sbContent.Length > 0 || finishReason != null)
                            break;
                        throw new TimeoutException("Streaming response timed out waiting for data");
                    }
                    
                    line = await readTask;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Read timeout - if we've received content or have a finish reason, we're done
                    LogStream($"OperationCanceled, content length={sbContent.Length}");
                    if (sbContent.Length > 0 || finishReason != null)
                        break;
                    throw new TimeoutException("Streaming response timed out waiting for data");
                }
                
                if (line is null)
                {
                    LogStream("Line is null - end of stream");
                    break; // End of stream
                }

                if (line.Length == 0) continue; // SSE event delimiter
                if (line.StartsWith("data: ") is false) continue;

                var payload = line.AsSpan(6).Trim().ToString();
                LogStream($"Payload: {payload}");
                
                if (payload == "[DONE]")
                {
                    LogStream("Received [DONE]");
                    break;
                }

                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;

                // choices[0]
                if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                {
                    // Some providers (OpenRouter) send a separate usage-only chunk with no choices
                    if (root.TryGetProperty("usage", out var usageOnlyEl) && usageOnlyEl.ValueKind == JsonValueKind.Object)
                    {
                        usage = JsonSerializer.Deserialize<Usage>(usageOnlyEl.GetRawText());
                        if (usage != null)
                        {
                            LogStream($"Got usage (no-choices chunk): prompt={usage.PromptTokens}, completion={usage.CompletionTokens}, total={usage.TotalTokens}");
                            onUsage?.Invoke(usage);
                        }
                    }
                    continue;
                }
                var choice = choices[0];
                if (root.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
                {
                    // Optional usage info
                    usage = JsonSerializer.Deserialize<Usage>(usageEl.GetRawText());
                    if(usage!=null) 
                    {
                        LogStream($"Got usage: prompt={usage.PromptTokens}, completion={usage.CompletionTokens}, total={usage.TotalTokens}");
                        onUsage?.Invoke(usage);
                    }
                }
                // finish_reason might show up on the last delta
                if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                {
                    finishReason = fr.GetString();
                    LogStream($"Got finish_reason: {finishReason}");
                    // If we have a finish reason like "stop" or "tool_calls", we should exit soon
                    // But first process any remaining delta content in this message
                }

                // delta
                if (!choice.TryGetProperty("delta", out var delta)) continue;

                // Reasoning content tokens (for thinking models like GLM 4.7, DeepSeek-R1)
                if (delta.TryGetProperty("reasoning_content", out var reasoningEl) && reasoningEl.ValueKind == JsonValueKind.String)
                {
                    var reasoningToken = reasoningEl.GetString();
                    if (!string.IsNullOrEmpty(reasoningToken))
                    {
                        onReasoningToken?.Invoke(reasoningToken);
                        sbReasoning.Append(reasoningToken);
                    }
                }

                // Content tokens
                if (delta.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
                {
                    var token = contentEl.GetString();
                    if (!string.IsNullOrEmpty(token))
                    {
                        onToken?.Invoke(token);
                        sbContent.Append(token);
                    }
                }

                // Tool call deltas
                if (delta.TryGetProperty("tool_calls", out var tcArr) && tcArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tc in tcArr.EnumerateArray())
                    {
                        // Each delta has an "index" to merge pieces
                        int index = tc.TryGetProperty("index", out var idxEl) && idxEl.ValueKind == JsonValueKind.Number ? idxEl.GetInt32() : 0;

                        if (!toolBuilders.TryGetValue(index, out var builder))
                        {
                            builder = (null, null, new StringBuilder());
                            toolBuilders[index] = builder;
                        }

                        if (tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                            builder.Id ??= idEl.GetString();

                        if (tc.TryGetProperty("function", out var fEl) && fEl.ValueKind == JsonValueKind.Object)
                        {
                            if (fEl.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                                builder.Name ??= nameEl.GetString();

                            if (fEl.TryGetProperty("arguments", out var argEl) && argEl.ValueKind == JsonValueKind.String)
                                builder.Args.Append(argEl.GetString());
                        }

                        toolBuilders[index] = builder;
                    }
                }
                
                // If we received a finish_reason, don't break yet — continue looping
                // to capture any subsequent usage-only chunks from the provider.
                // The [DONE] sentinel (line 203) will terminate the loop.
            }
            
            LogStream($"Exited loop, content length={sbContent.Length}, toolBuilders={toolBuilders.Count}");

            // Build final ToolCalls if any
            List<ToolCall>? toolCalls = null;
            if (toolBuilders.Count > 0)
            {
                toolCalls = new List<ToolCall>(toolBuilders.Count);
                foreach (var kv in toolBuilders.OrderBy(k => k.Key))
                {
                    var (id, name, args) = kv.Value;
                    toolCalls.Add(new ToolCall
                    {
                        Id = id ?? Guid.NewGuid().ToString("N"),
                        Type = "function",
                        Function = new FunctionCall
                        {
                            Name = name ?? "",
                            Arguments = args.ToString()
                        }
                    });
                }
            }

            return new StreamResult
            {
                Content = sbContent.ToString(),
                ReasoningContent = sbReasoning.Length > 0 ? sbReasoning.ToString() : null,
                ToolCalls = toolCalls,
                FinishReason = finishReason,
                Usage = usage
            };
        }

    }
}

