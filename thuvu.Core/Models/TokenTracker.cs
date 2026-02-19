using System;

namespace thuvu.Models
{
    /// <summary>
    /// Tracks token usage and warns when approaching context limits
    /// </summary>
    public class TokenTracker
    {
        private static TokenTracker? _instance;
        private static readonly object _lock = new();

        /// <summary>
        /// Maximum context length for current model
        /// </summary>
        public int MaxContextLength { get; set; } = 32768;

        /// <summary>
        /// Current total tokens used
        /// </summary>
        public int TotalTokens { get; private set; }

        /// <summary>
        /// Tokens used by system prompt
        /// </summary>
        public int SystemTokens { get; private set; }

        /// <summary>
        /// Tokens used by user messages
        /// </summary>
        public int UserTokens { get; private set; }

        /// <summary>
        /// Tokens used by assistant responses
        /// </summary>
        public int AssistantTokens { get; private set; }

        /// <summary>
        /// Tokens used by tool calls and results
        /// </summary>
        public int ToolTokens { get; private set; }

        /// <summary>
        /// Warning threshold percentage (default: 70%)
        /// </summary>
        public double WarningThreshold { get; set; } = 0.70;

        /// <summary>
        /// Critical threshold percentage (default: 85%)
        /// </summary>
        public double CriticalThreshold { get; set; } = 0.85;

        /// <summary>
        /// Auto-summarize threshold percentage (default: 90%)
        /// When exceeded, conversation is automatically summarized
        /// </summary>
        public double AutoSummarizeThreshold { get; set; } = 0.90;

        /// <summary>
        /// Truncation threshold percentage (default: 95%)
        /// When exceeded after summarization, older messages are truncated
        /// </summary>
        public double TruncationThreshold { get; set; } = 0.95;

        /// <summary>
        /// Whether auto-summarization is enabled
        /// </summary>
        public bool AutoSummarizeEnabled { get; set; } = true;

        /// <summary>
        /// Whether context needs summarization
        /// </summary>
        public bool NeedsSummarization => UsagePercent >= AutoSummarizeThreshold;

        /// <summary>
        /// Whether context needs truncation (last resort)
        /// </summary>
        public bool NeedsTruncation => UsagePercent >= TruncationThreshold;

        /// <summary>
        /// Singleton instance
        /// </summary>
        public static TokenTracker Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        _instance ??= new TokenTracker();
                    }
                }
                return _instance;
            }
        }

        /// <summary>
        /// Current usage as percentage (0.0 - 1.0)
        /// </summary>
        public double UsagePercent => MaxContextLength > 0 
            ? (double)TotalTokens / MaxContextLength 
            : 0;

        /// <summary>
        /// Remaining tokens available
        /// </summary>
        public int RemainingTokens => Math.Max(0, MaxContextLength - TotalTokens);

        /// <summary>
        /// Whether usage is at warning level
        /// </summary>
        public bool IsWarning => UsagePercent >= WarningThreshold && UsagePercent < CriticalThreshold;

        /// <summary>
        /// Whether usage is at critical level
        /// </summary>
        public bool IsCritical => UsagePercent >= CriticalThreshold;

        /// <summary>
        /// Last reported prompt tokens (context size)
        /// </summary>
        public int LastPromptTokens { get; private set; }

        /// <summary>
        /// Update token counts from API usage response
        /// </summary>
        public void UpdateFromUsage(int promptTokens, int completionTokens, int totalTokens)
        {
            lock (_lock)
            {
                // For context tracking, we use prompt_tokens + completion_tokens
                // This represents the context size AFTER the response (before next user input)
                // prompt_tokens = all messages sent to model
                // completion_tokens = new assistant response
                LastPromptTokens = promptTokens;
                TotalTokens = promptTokens + completionTokens; // Context after this turn
                AssistantTokens += completionTokens;
            }

            // Check thresholds and warn
            CheckThresholds();
        }
        
        /// <summary>
        /// Update from full Usage object (supports DeepSeek and other APIs)
        /// </summary>
        public void UpdateFromUsage(Usage usage)
        {
            if (usage == null) return;
            
            lock (_lock)
            {
                // Update max context length if API reports it
                if (usage.MaxContextLength.HasValue && usage.MaxContextLength.Value > 0)
                {
                    MaxContextLength = usage.MaxContextLength.Value;
                }
                
                LastPromptTokens = usage.PromptTokens;
                // Context after this turn = prompt + completion
                TotalTokens = usage.PromptTokens + usage.CompletionTokens;
                AssistantTokens += usage.CompletionTokens;
            }
            
            CheckThresholds();
        }

        /// <summary>
        /// Add tokens for a specific category
        /// </summary>
        public void AddTokens(TokenCategory category, int count)
        {
            lock (_lock)
            {
                switch (category)
                {
                    case TokenCategory.System:
                        SystemTokens += count;
                        break;
                    case TokenCategory.User:
                        UserTokens += count;
                        break;
                    case TokenCategory.Assistant:
                        AssistantTokens += count;
                        break;
                    case TokenCategory.Tool:
                        ToolTokens += count;
                        break;
                }
                TotalTokens = SystemTokens + UserTokens + AssistantTokens + ToolTokens;
            }

            CheckThresholds();
        }

        /// <summary>
        /// Reset all token counts
        /// </summary>
        public void Reset()
        {
            lock (_lock)
            {
                TotalTokens = 0;
                SystemTokens = 0;
                UserTokens = 0;
                AssistantTokens = 0;
                ToolTokens = 0;
            }
        }

        /// <summary>
        /// Estimate tokens for a string (rough approximation: ~4 chars per token)
        /// </summary>
        public static int EstimateTokens(string text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;
            
            // Rough estimate: average 4 characters per token for English
            // This is a simplification; actual tokenization varies by model
            return (int)Math.Ceiling(text.Length / 4.0);
        }

        /// <summary>
        /// Check thresholds and print warnings
        /// </summary>
        private void CheckThresholds()
        {
            if (IsCritical)
            {
                PrintCriticalWarning();
            }
            else if (IsWarning)
            {
                PrintWarning();
            }
        }

        /// <summary>
        /// Print warning when approaching limit
        /// </summary>
        private void PrintWarning()
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write("⚠️  Token Warning: ");
            Console.ResetColor();
            Console.WriteLine($"{UsagePercent:P0} of context used ({TotalTokens:N0}/{MaxContextLength:N0})");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("   Tip: Use /clear to reset conversation or /summarize to compress");
            Console.ResetColor();
        }

        /// <summary>
        /// Print critical warning when near limit
        /// </summary>
        private void PrintCriticalWarning()
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Write("🚨 Token Critical: ");
            Console.ResetColor();
            Console.WriteLine($"{UsagePercent:P0} of context used ({TotalTokens:N0}/{MaxContextLength:N0})");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("   Only {0:N0} tokens remaining! Use /clear to continue.", RemainingTokens);
            Console.ResetColor();
        }

        /// <summary>
        /// Print current token usage status bar
        /// </summary>
        public void PrintStatus()
        {
            var barWidth = 40;
            var filledWidth = (int)(UsagePercent * barWidth);
            filledWidth = Math.Min(barWidth, Math.Max(0, filledWidth));

            Console.WriteLine();
            Console.Write("Token Usage: ");
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write($"{TotalTokens:N0}");
            Console.ResetColor();
            Console.Write(" / ");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"{MaxContextLength:N0}");
            Console.ResetColor();
            Console.WriteLine($" ({UsagePercent:P0})");

            // Progress bar
            Console.Write("  ");
            
            var barColor = IsCritical ? ConsoleColor.Red 
                         : IsWarning ? ConsoleColor.Yellow 
                         : ConsoleColor.Green;

            Console.ForegroundColor = barColor;
            Console.Write(new string('█', filledWidth));
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write(new string('░', barWidth - filledWidth));
            Console.ResetColor();
            Console.WriteLine();

            // Breakdown
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  System: {SystemTokens:N0} | User: {UserTokens:N0} | Assistant: {AssistantTokens:N0} | Tools: {ToolTokens:N0}");
            Console.ResetColor();
            Console.WriteLine();
        }

        /// <summary>
        /// Print compact status (for status bar)
        /// </summary>
        public string GetCompactStatus()
        {
            var icon = IsCritical ? "🚨" : IsWarning ? "⚠️" : "📊";
            return $"{icon} {UsagePercent:P0} ({TotalTokens:N0}/{MaxContextLength:N0})";
        }
    }

    /// <summary>
    /// Token categories for tracking
    /// </summary>
    public enum TokenCategory
    {
        System,
        User,
        Assistant,
        Tool
    }
}
