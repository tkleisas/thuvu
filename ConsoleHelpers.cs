using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using CodingAgent;
using thuvu.Models;

namespace thuvu
{
    /// <summary>
    /// Console output utilities for formatting and display
    /// </summary>
    public static class ConsoleHelpers
    {
        // ═══════════════════════════════════════════════════════════════════════
        // Box drawing and decorative characters
        // ═══════════════════════════════════════════════════════════════════════
        public const string BoxTopLeft = "╔";
        public const string BoxTopRight = "╗";
        public const string BoxBottomLeft = "╚";
        public const string BoxBottomRight = "╝";
        public const string BoxHorizontal = "═";
        public const string BoxVertical = "║";
        public const string BoxTeeLeft = "╠";
        public const string BoxTeeRight = "╣";

        // Status icons
        public const string IconSuccess = "✓";
        public const string IconError = "✗";
        public const string IconWarning = "⚠";
        public const string IconInfo = "ℹ";
        public const string IconTool = "🔧";
        public const string IconThinking = "💭";
        public const string IconSend = "➤";
        public const string IconReceive = "◀";
        public const string IconUser = "👤";
        public const string IconBot = "🤖";
        public const string IconClock = "⏱";
        public const string IconTokens = "🎫";
        public const string IconSpinner = "◐◓◑◒";

        /// <summary>
        /// Print a styled header box
        /// </summary>
        public static void PrintHeader(string text, ConsoleColor color = ConsoleColor.Cyan)
        {
            var width = Math.Max(text.Length + 4, 40);
            var line = new string('═', width - 2);
            var padding = new string(' ', (width - 2 - text.Length) / 2);
            var paddingRight = new string(' ', width - 2 - text.Length - padding.Length);

            WithColor(color, () =>
            {
                Console.WriteLine($"╔{line}╗");
                Console.WriteLine($"║{padding}{text}{paddingRight}║");
                Console.WriteLine($"╚{line}╝");
            });
        }

        /// <summary>
        /// Print a styled section divider
        /// </summary>
        public static void PrintDivider(string title = "", ConsoleColor color = ConsoleColor.DarkGray)
        {
            var width = Console.WindowWidth > 10 ? Console.WindowWidth - 1 : 80;
            WithColor(color, () =>
            {
                if (string.IsNullOrEmpty(title))
                {
                    Console.WriteLine(new string('─', width));
                }
                else
                {
                    var left = "───[ ";
                    var right = " ]";
                    var remaining = width - left.Length - title.Length - right.Length;
                    Console.WriteLine($"{left}{title}{right}{new string('─', Math.Max(0, remaining))}");
                }
            });
        }

        /// <summary>
        /// Print a success message with icon
        /// </summary>
        public static void PrintSuccess(string message)
        {
            WithColor(ConsoleColor.Green, () => Console.Write($" {IconSuccess} "));
            Console.WriteLine(message);
        }

        /// <summary>
        /// Print an error message with icon
        /// </summary>
        public static void PrintError(string message)
        {
            WithColor(ConsoleColor.Red, () => Console.Write($" {IconError} "));
            WithColor(ConsoleColor.Red, () => Console.WriteLine(message));
        }

        /// <summary>
        /// Print a warning message with icon
        /// </summary>
        public static void PrintWarning(string message)
        {
            WithColor(ConsoleColor.Yellow, () => Console.Write($" {IconWarning} "));
            WithColor(ConsoleColor.Yellow, () => Console.WriteLine(message));
        }

        /// <summary>
        /// Print an info message with icon
        /// </summary>
        public static void PrintInfo(string message)
        {
            WithColor(ConsoleColor.Cyan, () => Console.Write($" {IconInfo} "));
            Console.WriteLine(message);
        }

        /// <summary>
        /// Print a tool execution message
        /// </summary>
        public static void PrintToolCall(string toolName, string args, string? result = null)
        {
            WithColor(ConsoleColor.Magenta, () => Console.Write($" {IconTool} "));
            WithColor(ConsoleColor.White, () => Console.Write(toolName));
            WithColor(ConsoleColor.DarkGray, () => Console.WriteLine($" ({args})"));
            if (result != null)
            {
                // Show full result - let it scroll naturally
                WithColor(ConsoleColor.DarkGray, () => Console.WriteLine($"    └─ {result}"));
            }
        }

        /// <summary>
        /// Print a timestamp with icon
        /// </summary>
        public static void PrintTimestamp(string message = "")
        {
            WithColor(ConsoleColor.DarkGray, () => Console.Write($"[{DateTime.Now:HH:mm:ss}] "));
            if (!string.IsNullOrEmpty(message))
                Console.WriteLine(message);
        }

        /// <summary>
        /// Print token usage information
        /// </summary>
        public static void PrintTokenUsage(int prompt, int completion, int total)
        {
            WithColor(ConsoleColor.DarkCyan, () => Console.Write($" {IconTokens} "));
            WithColor(ConsoleColor.DarkGray, () => Console.Write("tokens: "));
            WithColor(ConsoleColor.Blue, () => Console.Write($"prompt={prompt}"));
            WithColor(ConsoleColor.DarkGray, () => Console.Write(", "));
            WithColor(ConsoleColor.Green, () => Console.Write($"completion={completion}"));
            WithColor(ConsoleColor.DarkGray, () => Console.Write(", "));
            WithColor(ConsoleColor.Cyan, () => Console.WriteLine($"total={total}"));
        }

        /// <summary>
        /// Print the user prompt indicator
        /// </summary>
        public static void PrintPrompt()
        {
            WithColor(ConsoleColor.Green, () => Console.Write($"{IconUser} "));
            WithColor(ConsoleColor.White, () => Console.Write("> "));
        }

        /// <summary>
        /// Print bot response indicator
        /// </summary>
        public static void PrintBotIndicator()
        {
            WithColor(ConsoleColor.Cyan, () => Console.Write($"{IconBot} "));
        }

        /// <summary>
        /// Print a status message (sending, receiving, etc.)
        /// </summary>
        public static void PrintStatus(string message, ConsoleColor color = ConsoleColor.DarkGray)
        {
            WithColor(color, () =>
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
            });
        }

        /// <summary>
        /// Print a styled key-value pair
        /// </summary>
        public static void PrintKeyValue(string key, string value, ConsoleColor keyColor = ConsoleColor.Gray, ConsoleColor valueColor = ConsoleColor.White)
        {
            WithColor(keyColor, () => Console.Write($"  {key}: "));
            WithColor(valueColor, () => Console.WriteLine(value));
        }

        /// <summary>
        /// Print a styled list item
        /// </summary>
        public static void PrintListItem(string text, int indent = 2)
        {
            var padding = new string(' ', indent);
            WithColor(ConsoleColor.DarkGray, () => Console.Write($"{padding}• "));
            Console.WriteLine(text);
        }

        // ═══════════════════════════════════════════════════════════════════════
        // Streaming animation helpers
        // ═══════════════════════════════════════════════════════════════════════
        
        private static readonly string[] SpinnerFrames = new[] { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };
        private static readonly string[] ThinkingFrames = new[] { "💭", "💬", "💭", "💬" };
        private static int _spinnerIndex = 0;
        private static int _thinkingIndex = 0;
        private static DateTime _streamStartTime;
        private static int _tokenCount = 0;
        private static readonly object _spinnerLock = new();

        /// <summary>
        /// Initialize streaming state
        /// </summary>
        public static void StartStreaming()
        {
            lock (_spinnerLock)
            {
                _streamStartTime = DateTime.Now;
                _tokenCount = 0;
                _spinnerIndex = 0;
                _thinkingIndex = 0;
            }
        }

        /// <summary>
        /// Get elapsed streaming time
        /// </summary>
        public static TimeSpan GetStreamingElapsed()
        {
            lock (_spinnerLock)
            {
                return DateTime.Now - _streamStartTime;
            }
        }

        /// <summary>
        /// Increment token count and return current count
        /// </summary>
        public static int IncrementTokenCount(int count = 1)
        {
            lock (_spinnerLock)
            {
                _tokenCount += count;
                return _tokenCount;
            }
        }

        /// <summary>
        /// Get current token count
        /// </summary>
        public static int GetTokenCount()
        {
            lock (_spinnerLock)
            {
                return _tokenCount;
            }
        }

        /// <summary>
        /// Print a "thinking" indicator (waiting for first token)
        /// </summary>
        public static void PrintThinkingIndicator()
        {
            lock (_spinnerLock)
            {
                _thinkingIndex = (_thinkingIndex + 1) % ThinkingFrames.Length;
            }
            var elapsed = GetStreamingElapsed();
            
            // Save cursor position, print indicator, restore position
            var frame = ThinkingFrames[_thinkingIndex];
            WithColor(ConsoleColor.Yellow, () => Console.Write($"\r {frame} Thinking... ({elapsed.TotalSeconds:F1}s) "));
        }

        /// <summary>
        /// Clear the thinking indicator line
        /// </summary>
        public static void ClearThinkingIndicator()
        {
            Console.Write("\r" + new string(' ', 40) + "\r");
        }

        /// <summary>
        /// Print streaming progress indicator
        /// </summary>
        public static void PrintStreamingProgress()
        {
            lock (_spinnerLock)
            {
                _spinnerIndex = (_spinnerIndex + 1) % SpinnerFrames.Length;
            }
            var elapsed = GetStreamingElapsed();
            var tokens = GetTokenCount();
            var tokensPerSec = elapsed.TotalSeconds > 0 ? tokens / elapsed.TotalSeconds : 0;
            
            // Move to status line and print progress
            WithColor(ConsoleColor.DarkGray, () => 
            {
                Console.Write($" {SpinnerFrames[_spinnerIndex]} {tokens} tokens ({tokensPerSec:F1} t/s) ");
            });
        }

        /// <summary>
        /// Print streaming header with animation
        /// </summary>
        public static void PrintStreamingHeader(string modelName)
        {
            StartStreaming();
            Console.WriteLine();
            WithColor(ConsoleColor.Cyan, () => Console.Write("╭─── "));
            WithColor(ConsoleColor.White, () => Console.Write($"🤖 {modelName}"));
            WithColor(ConsoleColor.Cyan, () => Console.WriteLine(" ───────────────────────────────────────"));
            WithColor(ConsoleColor.Cyan, () => Console.Write("│ "));
        }

        /// <summary>
        /// Print streaming footer with stats
        /// </summary>
        public static void PrintStreamingFooter()
        {
            var elapsed = GetStreamingElapsed();
            var tokens = GetTokenCount();
            var tokensPerSec = elapsed.TotalSeconds > 0 ? tokens / elapsed.TotalSeconds : 0;
            
            Console.WriteLine();
            WithColor(ConsoleColor.Cyan, () => Console.Write("╰─── "));
            WithColor(ConsoleColor.DarkGray, () => Console.Write($"⏱ {elapsed.TotalSeconds:F1}s │ "));
            WithColor(ConsoleColor.DarkGray, () => Console.Write($"📝 ~{tokens} tokens │ "));
            WithColor(ConsoleColor.DarkGray, () => Console.Write($"⚡ {tokensPerSec:F1} t/s"));
            WithColor(ConsoleColor.Cyan, () => Console.WriteLine(" ───"));
        }

        /// <summary>
        /// Print a single streaming token with visual feedback
        /// </summary>
        public static void PrintStreamingToken(string token)
        {
            IncrementTokenCount(1);
            Console.Write(token);
        }


        /// <summary>
        /// Print a unified diff with syntax highlighting
        /// </summary>
        public static void PrintUnifiedDiff(string patch)
        {
            if (string.IsNullOrEmpty(patch))
            {
                Console.WriteLine("(no diff)");
                return;
            }

            var lines = patch.Replace("\r\n", "\n").Split('\n');
            foreach (var raw in lines)
            {
                var line = raw;

                if (line.StartsWith("diff --git "))
                {
                    WithColor(ConsoleColor.Yellow, () => Console.WriteLine(line));
                }
                else if (line.StartsWith("--- ") || line.StartsWith("+++ "))
                {
                    WithColor(ConsoleColor.Cyan, () => Console.WriteLine(line));
                }
                else if (line.StartsWith("@@"))
                {
                    WithColor(ConsoleColor.Magenta, () => Console.WriteLine(line));
                }
                else if (line.StartsWith("+") && !line.StartsWith("+++ "))
                {
                    WithColor(ConsoleColor.Green, () => Console.WriteLine(line));
                }
                else if (line.StartsWith("-") && !line.StartsWith("--- "))
                {
                    WithColor(ConsoleColor.Red, () => Console.WriteLine(line));
                }
                else
                {
                    Console.WriteLine(line);
                }
            }
        }

        /// <summary>
        /// Execute an action with a specific console color
        /// </summary>
        public static void WithColor(ConsoleColor color, Action act)
        {
            var prev = Console.ForegroundColor;
            try { Console.ForegroundColor = color; act(); }
            finally { Console.ForegroundColor = prev; }
        }

        /// <summary>
        /// Auto pretty-print tool results based on content type
        /// </summary>
        public static void AutoPrettyPrinterCallback(string toolName, string toolResultJson)
        {
            try
            {
                using var doc = JsonDocument.Parse(toolResultJson);
                var root = doc.RootElement;

                // Look for stdout-bearing tools (run_process, git_*, dotnet_*)
                if (root.TryGetProperty("stdout", out var stdoutEl) && stdoutEl.ValueKind == JsonValueKind.String)
                {
                    var stdout = stdoutEl.GetString() ?? "";

                    // Diffs: git_diff or anything that looks like a unified diff
                    if (toolName == "git_diff" || LooksLikeUnifiedDiff(stdout))
                    {
                        Console.WriteLine();
                        PrintUnifiedDiff(stdout);
                        Console.WriteLine();
                        return;
                    }

                    // Tests: dotnet_test or output that looks like a test summary
                    if (toolName == "dotnet_test" ||
                        stdout.IndexOf("Failed:", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        stdout.IndexOf("Passed!", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        stdout.IndexOf("Failed!", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var summary = TestSummary.ParseDotnetTestStdout(stdout);
                        if (summary == null)
                        {
                            var trx = TestSummary.TryFindTrxPathFromStdoutOrFS(stdout);
                            if (trx != null) summary = TestSummary.ParseTrxSummary(trx);
                        }
                        if (summary != null)
                        {
                            Console.WriteLine();
                            TestSummary.PrintTestSummary(summary);
                            Console.WriteLine();
                            return;
                        }
                    }
                }
            }
            catch
            {
                // Non-fatal: never let UI helpers crash the agent loop
            }
        }

        /// <summary>
        /// Check if text looks like a unified diff
        /// </summary>
        public static bool LooksLikeUnifiedDiff(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var lines = text.Replace("\r\n", "\n").Split('\n');

            // Strong signals
            if (lines.Any(l => l.StartsWith("diff --git ", StringComparison.Ordinal))) return true;

            // Hunk markers plus +/- lines
            int hunks = lines.Count(l => l.StartsWith("@@"));
            if (hunks > 0)
            {
                int adds = lines.Count(l => l.StartsWith("+") && !l.StartsWith("+++ "));
                int dels = lines.Count(l => l.StartsWith("-") && !l.StartsWith("--- "));
                if (adds + dels > 0) return true;
            }

            // File headers only
            if (lines.Any(l => l.StartsWith("--- ")) && lines.Any(l => l.StartsWith("+++ "))) return true;

            return false;
        }

        /// <summary>
        /// Split a command line into tokens, respecting quotes
        /// </summary>
        public static List<string> TokenizeArgs(string text)
        {
            var res = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return res;

            var sb = new System.Text.StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"') { inQuotes = !inQuotes; continue; }
                if (char.IsWhiteSpace(c) && !inQuotes)
                {
                    if (sb.Length > 0) { res.Add(sb.ToString()); sb.Clear(); }
                }
                else sb.Append(c);
            }
            if (sb.Length > 0) res.Add(sb.ToString());
            return res;
        }

        /// <summary>
        /// Print help text for all commands
        /// </summary>
        public static void PrintHelp()
        {
            PrintHeader("T.H.U.V.U. Help", ConsoleColor.Cyan);
            Console.WriteLine();
            WithColor(ConsoleColor.DarkGray, () => Console.WriteLine("(T)ool for (H)eurustic (U)niversal (V)ersatile (U)sage"));
            Console.WriteLine();

            // Basic Commands
            PrintDivider("Basic Commands", ConsoleColor.Yellow);
            PrintHelpCommand("/help", "Show this help");
            PrintHelpCommand("/exit", "Quit");
            PrintHelpCommand("/clear", "Reset conversation (keeps current system prompt)");
            PrintHelpCommand("/system <text>", "Set system prompt");
            PrintHelpCommand("/stream on|off", "Toggle token-by-token streaming");
            Console.WriteLine();

            // Development Commands
            PrintDivider("Development Commands", ConsoleColor.Yellow);
            PrintHelpCommand("/diff [--staged] [--context N] [--root PATH] [PATH ...]", "Show a git unified diff");
            PrintHelpCommand("/test [PROJECT] [--filter EXP] [--logger trx|console]", "Run dotnet tests");
            PrintHelpCommand("/run CMD [ARGS ...] [--cwd PATH] [--timeout MS]", "Run whitelisted command");
            Console.WriteLine();

            // Git Commands
            PrintDivider("Git Commands", ConsoleColor.Yellow);
            PrintHelpCommand("/commit \"msg\" [--all] [--staged] [--no-test]", "Commit with test gate");
            PrintHelpCommand("/push [--remote NAME] [--branch NAME] [--force-with-lease]", "Safe push");
            PrintHelpCommand("/pull [--remote NAME] [--merge] [--ff-only] [--prune]", "Safe pull with autostash");
            Console.WriteLine();

            // Configuration
            PrintDivider("Configuration", ConsoleColor.Yellow);
            PrintHelpCommand("/config [show|path|reload|save]", "Manage config file");
            PrintHelpCommand("/set model <id>", "Change model");
            PrintHelpCommand("/set host <url>", "Change LM Studio host URL");
            PrintHelpCommand("/set stream on|off", "Toggle streaming");
            PrintHelpCommand("/set timeout <ms>", "Set process timeout");
            Console.WriteLine();

            // RAG
            PrintDivider("RAG (Retrieval-Augmented Generation)", ConsoleColor.Magenta);
            PrintHelpCommand("/rag config", "Show RAG configuration");
            PrintHelpCommand("/rag enable|disable", "Enable/disable RAG");
            PrintHelpCommand("/rag index PATH [--recursive] [--pattern GLOB]", "Index files");
            PrintHelpCommand("/rag search QUERY [--top N]", "Semantic search");
            PrintHelpCommand("/rag clear [PATH]", "Clear index");
            Console.WriteLine();

            // MCP
            PrintDivider("MCP (Model Context Protocol)", ConsoleColor.Magenta);
            PrintHelpCommand("/mcp config", "Show MCP configuration");
            PrintHelpCommand("/mcp enable|disable", "Enable/disable MCP");
            PrintHelpCommand("/mcp on|off", "Activate/deactivate MCP mode");
            PrintHelpCommand("/mcp check", "Check MCP environment");
            PrintHelpCommand("/mcp run \"<code>\"", "Execute TypeScript in sandbox");
            PrintHelpCommand("/mcp skill list|run|save|delete", "Manage saved skills");
            Console.WriteLine();

            // Task/Session Management (MVP)
            PrintDivider("Task Management", ConsoleColor.Magenta);
            PrintHelpCommand("/task start <description>", "Start new task with git branch isolation");
            PrintHelpCommand("/task status", "Show current task status");
            PrintHelpCommand("/task complete [-m] [-d]", "Complete task (--merge, --delete branch)");
            PrintHelpCommand("/task abort", "Abort task and discard changes");
            PrintHelpCommand("/checkpoint [msg] [-t]", "Create checkpoint (--test to run tests)");
            PrintHelpCommand("/rollback [target]", "Rollback to checkpoint or commit");
            Console.WriteLine();

            // System Status
            PrintDivider("System Status", ConsoleColor.Cyan);
            PrintHelpCommand("/health", "Run health checks on all services");
            PrintHelpCommand("/status", "Show session and token status");
            PrintHelpCommand("/tokens", "Show token usage breakdown");
            PrintHelpCommand("/tokens reset", "Reset conversation and tokens");
            PrintHelpCommand("/tokens budget <n>", "Set max token budget");
            PrintHelpCommand("/summarize", "Summarize conversation to reduce context");
            Console.WriteLine();

            // Multi-Model Support
            PrintDivider("Multi-Model Support", ConsoleColor.Blue);
            PrintHelpCommand("/models list", "List all configured models");
            PrintHelpCommand("/models use <id>", "Switch to a specific model");
            PrintHelpCommand("/models thinking [id]", "Get/set thinking model for planning");
            PrintHelpCommand("/models coding [id]", "Get/set coding model for simple tasks");
            Console.WriteLine();

            // Permission System
            PrintDivider("Permission System", ConsoleColor.Green);
            WithColor(ConsoleColor.DarkGray, () => Console.WriteLine("  Read-only tools are always allowed. Write tools prompt:"));
            WithColor(ConsoleColor.Green, () => Console.Write("    [A] "));
            Console.WriteLine("Always for this repo (persistent)");
            WithColor(ConsoleColor.Yellow, () => Console.Write("    [S] "));
            Console.WriteLine("For this session (temporary)");
            WithColor(ConsoleColor.Cyan, () => Console.Write("    [O] "));
            Console.WriteLine("Once (this time only)");
            WithColor(ConsoleColor.Red, () => Console.Write("    [N] "));
            Console.WriteLine("No (cancel operation)");
            Console.WriteLine();
        }

        /// <summary>
        /// Print a formatted help command entry
        /// </summary>
        private static void PrintHelpCommand(string command, string description)
        {
            WithColor(ConsoleColor.Green, () => Console.Write($"  {command}"));
            var padding = Math.Max(1, 55 - command.Length);
            Console.Write(new string(' ', padding));
            WithColor(ConsoleColor.Gray, () => Console.WriteLine(description));
        }
    }
}
