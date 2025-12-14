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
            Action<string, string>? onToolResult = null)
        {
            while (true)
            {
                var req = new ChatRequest
                {
                    Model = model,
                    Messages = messages,
                    Tools = tools,
                    ToolChoice = "auto",
                    Temperature = 0.2
                };

                using var resp = await http.PostAsJsonAsync("/v1/chat/completions", req, JsonOpts, ct);
                resp.EnsureSuccessStatusCode();

                var body = await resp.Content.ReadFromJsonAsync<ChatResponse>(JsonOpts, ct)
                           ?? throw new InvalidOperationException("Empty response.");
                if (body?.Usage is { } u)
                    ConsoleHelpers.PrintTokenUsage(u.PromptTokens, u.CompletionTokens, u.TotalTokens);
                var msg = body.Choices[0].Message;

                if (msg.ToolCalls is { Count: > 0 })
                {
                    messages.Add(msg);

                    foreach (var call in msg.ToolCalls)
                    {
                        var name = call.Function.Name;
                        var argsJson = call.Function.Arguments ?? "{}";
                        var toolResult = await ToolExecutor.ExecuteToolAsync(name, argsJson, ct);
                        ConsoleHelpers.PrintToolCall(name, argsJson, toolResult);
                        onToolResult?.Invoke(name, toolResult);

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
            Action<Usage>? onUsage = null)
        {
            while (true)
            {
                var req = new ChatRequest
                {
                    Model = model,
                    Messages = messages,
                    Tools = tools,
                    ToolChoice = "auto",
                    Temperature = 0.2
                };

                LogAgent("Calling StreamChatOnceAsync...");
                var result = await StreamResult.StreamChatOnceAsync(http, req, ct, onToken, onUsage);
                LogAgent($"StreamChatOnceAsync returned, ToolCalls={result.ToolCalls?.Count ?? 0}, Content length={result.Content?.Length ?? 0}");

                if (result.ToolCalls is { Count: > 0 })
                {
                    LogAgent($"Processing {result.ToolCalls.Count} tool calls");
                    messages.Add(new ChatMessage
                    {
                        Role = "assistant",
                        Content = null,
                        ToolCalls = result.ToolCalls
                    });

                    foreach (var call in result.ToolCalls)
                    {
                        var name = call.Function.Name;
                        var argsJson = call.Function.Arguments ?? "{}";
                        LogAgent($"Executing tool: {name}");
                        var toolResult = await ToolExecutor.ExecuteToolAsync(name, argsJson, ct);
                        LogAgent($"Tool {name} completed, result length={toolResult.Length}");
                        ConsoleHelpers.PrintToolCall(name, argsJson, toolResult);
                        onToolResult?.Invoke(name, toolResult);

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
