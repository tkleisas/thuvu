using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Terminal.Gui;
using thuvu.Models;
using CodingAgent;
using thuvu.Tools;

namespace thuvu.Tui
{
    /// <summary>
    /// Handles chat interactions with the LLM for the TUI interface
    /// </summary>
    public class TuiChatHandler
    {
        private readonly HttpClient _http;
        private readonly List<Tool> _tools;
        private readonly Action<string> _appendToActionView;
        private readonly Action<string, bool> _appendText;
        private readonly ToolProgressCallback _updateToolProgress;
        private readonly Action<string> _updateWorkLabel;
        private CancellationTokenSource? _thinkingAnimationCts;
        
        public TuiChatHandler(
            HttpClient http,
            List<Tool> tools,
            Action<string> appendToActionView,
            Action<string, bool> appendText,
            ToolProgressCallback updateToolProgress,
            Action<string> updateWorkLabel)
        {
            _http = http;
            _tools = tools;
            _appendToActionView = appendToActionView;
            _appendText = appendText;
            _updateToolProgress = updateToolProgress;
            _updateWorkLabel = updateWorkLabel;
        }
        
        public async Task<string?> ProcessChatAsync(List<ChatMessage> messages, CancellationToken ct)
        {
            string? final;
            
            if (AgentConfig.Config.Stream)
            {
                final = await ProcessStreamingChatAsync(messages, ct);
            }
            else
            {
                final = await ProcessNonStreamingChatAsync(messages, ct);
            }
            
            return final;
        }
        
        private async Task<string?> ProcessStreamingChatAsync(List<ChatMessage> messages, CancellationToken ct)
        {
            bool receivedTokens = false;
            var tokenBuffer = new System.Text.StringBuilder();
            int tokenCount = 0;
            
            _thinkingAnimationCts?.Cancel();
            _thinkingAnimationCts = new CancellationTokenSource();
            var thinkingToken = _thinkingAnimationCts.Token;
            var startTime = DateTime.Now;
            
            // Start thinking animation
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!thinkingToken.IsCancellationRequested && !receivedTokens)
                    {
                        var elapsed = (DateTime.Now - startTime).TotalSeconds;
                        _updateWorkLabel($"Waiting {elapsed:F0}s...");
                        await Task.Delay(500, thinkingToken);
                    }
                }
                catch (OperationCanceledException) { }
            }, thinkingToken);
            
            try
            {
                var inReasoning = false;
                var final = await AgentLoop.CompleteWithToolsStreamingAsync(
                    _http, AgentConfig.Config.Model, messages, _tools, ct,
                    onToken: token => 
                    {
                        if (!receivedTokens)
                        {
                            receivedTokens = true;
                            _thinkingAnimationCts?.Cancel();
                            // End reasoning section if we were in it
                            if (inReasoning)
                            {
                                _appendToActionView("\n[/Thinking]\n\n");
                                inReasoning = false;
                            }
                            _appendToActionView("ASSISTANT> ");
                        }
                        
                        tokenCount++;
                        tokenBuffer.Append(token);
                        
                        if (tokenBuffer.Length > 10 || token.Contains('\n'))
                        {
                            var bufferedText = tokenBuffer.ToString();
                            tokenBuffer.Clear();
                            _appendToActionView(bufferedText);
                        }
                    },
                    onToolResult: (name, result) =>
                    {
                        _thinkingAnimationCts?.Cancel();
                        if (tokenBuffer.Length > 0)
                        {
                            var bufferedText = tokenBuffer.ToString();
                            tokenBuffer.Clear();
                            _appendToActionView(bufferedText + "\n");
                        }
                    },
                    onUsage: usage =>
                    {
                        _updateWorkLabel($"Tokens: {usage.TotalTokens}");
                    },
                    onToolComplete: (name, args, result, elapsed) =>
                    {
                        AppendToolText(name, result, elapsed);
                    },
                    onToolProgress: _updateToolProgress,
                    onReasoningToken: token =>
                    {
                        if (!receivedTokens)
                        {
                            receivedTokens = true;
                            _thinkingAnimationCts?.Cancel();
                        }
                        if (!inReasoning)
                        {
                            inReasoning = true;
                            _appendToActionView("💭 [Thinking] ");
                        }
                        _appendToActionView(token);
                    }
                );
                
                // Flush remaining buffer
                if (tokenBuffer.Length > 0)
                {
                    var bufferedText = tokenBuffer.ToString();
                    _appendToActionView(bufferedText);
                }
                
                _appendToActionView("\n");
                return final;
            }
            finally
            {
                _thinkingAnimationCts?.Cancel();
                _thinkingAnimationCts?.Dispose();
                _thinkingAnimationCts = null;
            }
        }
        
        private async Task<string?> ProcessNonStreamingChatAsync(List<ChatMessage> messages, CancellationToken ct)
        {
            return await AgentLoop.CompleteWithToolsAsync(
                _http, AgentConfig.Config.Model, messages, _tools, ct,
                onToolResult: null,
                onToolComplete: (name, args, result, elapsed) => AppendToolText(name, result, elapsed),
                onToolProgress: _updateToolProgress
            );
        }
        
        private void AppendToolText(string toolName, string result, TimeSpan? elapsed)
        {
            var statusIcon = result.Contains("\"error\"") || result.Contains("\"timed_out\":true") ? "[X]" : "[OK]";
            var elapsedStr = elapsed.HasValue ? $" ({FormatElapsed(elapsed.Value)})" : "";
            _appendToActionView($"  TOOL {statusIcon} {toolName}{elapsedStr}\n");
        }
        
        private static string FormatElapsed(TimeSpan elapsed)
        {
            if (elapsed.TotalMinutes >= 1)
                return $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s";
            return $"{elapsed.TotalSeconds:F1}s";
        }
        
        public void CancelThinkingAnimation()
        {
            _thinkingAnimationCts?.Cancel();
        }
    }
}
