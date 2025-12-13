using CodingAgent;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using thuvu.Models;
using thuvu.Tools;

namespace thuvu
{
    /// <summary>
    /// Main entry point for THUVU coding agent
    /// </summary>
    internal class Program
    {
        private static int _currentContextLength = 0;

        public static async Task Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine(AppContext.BaseDirectory);

            // Initialize logging
            AgentLogger.Initialize();
            AgentLogger.LogInfo("THUVU starting...");

            // Load configurations
            AgentConfig.LoadConfig();
            RagConfig.LoadConfig();
            McpConfig.LoadConfig();

            // Check if TUI mode is requested
            bool useTui = args.Length > 0 && args[0].Equals("--tui", StringComparison.OrdinalIgnoreCase);

            // Initialize permission manager with work directory
            PermissionManager.SetCurrentRepoPath(AgentConfig.GetWorkDirectory());

            using var http = new HttpClient();
            AgentConfig.ApplyConfig(http);

            // Initialize RAG service
            RagToolImpl.Initialize(http);

            try
            {
                _currentContextLength = (int)(await AgentLoop.GetContextLengthAsync(http, AgentConfig.Config.Model, CancellationToken.None) ?? 4096);
            }
            catch (Exception ex)
            {
                AgentLogger.LogWarning("Could not connect to LLM service: {Message}. Using default context length.", ex.Message);
                _currentContextLength = 4096;
            }

            // Conversation state - use appropriate system prompt
            var messages = new List<ChatMessage>
            {
                new("system", McpSystemPrompts.GetSystemPrompt(McpConfig.Instance.McpModeActive))
            };

            // Tools you expose to the model
            var tools = BuildTools.GetBuildTools();

            if (useTui)
            {
                // Use Terminal.GUI interface
                var tuiInterface = new TuiInterface(http, tools, messages);
                tuiInterface.Run();
                return;
            }

            // Original console interface
            Console.WriteLine("T.H.U.V.U. coding agent (C) 2025 " + Helpers.GetCurrentGitTag());
            Console.WriteLine("type /exit to quit, /help for full list of commands, or --tui for Terminal UI");
            Console.WriteLine($"Config file: {AgentConfig.GetConfigPath()}");
            Console.WriteLine($"Model: {AgentConfig.Config.Model}");
            Console.WriteLine($"Host:  {AgentConfig.Config.HostUrl}");
            Console.WriteLine($"Work directory: {AgentConfig.GetWorkDirectory()}");
            Console.WriteLine($"Streaming responses: {(AgentConfig.Config.Stream ? "ON" : "OFF")}");
            Console.WriteLine();

            while (true)
            {
                Console.Write("> ");
                var user = Console.ReadLine();
                if (user == null) continue;

                // Handle commands
                if (await TryHandleCommandAsync(user, messages, http, CancellationToken.None))
                    continue;

                if (user.Equals("/exit", StringComparison.OrdinalIgnoreCase))
                {
                    PermissionManager.ClearSessionPermissions();
                    break;
                }

                if (string.IsNullOrWhiteSpace(user)) continue;

                messages.Add(new ChatMessage("user", user));

                // Send to LM Studio and run tool loop until final answer
                string? final;

                if (AgentConfig.Config.Stream)
                {
                    final = await AgentLoop.CompleteWithToolsStreamingAsync(
                        http, AgentConfig.Config.Model, messages, tools, CancellationToken.None,
                        onToken: token => Console.Write(token),
                        onToolResult: ConsoleHelpers.AutoPrettyPrinterCallback,
                        onUsage: u => Console.WriteLine($"\n[tokens] prompt={u.PromptTokens}, completion={u.CompletionTokens}, total={u.TotalTokens}")
                    );
                    Console.WriteLine();
                }
                else
                {
                    final = await AgentLoop.CompleteWithToolsAsync(
                        http, AgentConfig.Config.Model, messages, tools, CancellationToken.None,
                        onToolResult: ConsoleHelpers.AutoPrettyPrinterCallback
                    );
                }

                if (!string.IsNullOrEmpty(final))
                {
                    // Only print final content if not streaming (already printed via onToken)
                    if (!AgentConfig.Config.Stream)
                    {
                        Console.WriteLine();
                        Console.WriteLine(final);
                        Console.WriteLine();
                    }
                    messages.Add(new ChatMessage("assistant", final));

                    // If MCP mode is active, check for TypeScript code blocks and execute them
                    if (McpConfig.Instance.McpModeActive)
                    {
                        var codeResult = await CommandHandlers.TryExecuteMcpCodeBlockAsync(final, CancellationToken.None);
                        if (codeResult != null)
                        {
                            messages.Add(new ChatMessage("user", $"Code execution result:\n{codeResult}"));
                        }
                    }
                }
                else
                {
                    Console.WriteLine("(no content)");
                }
            }
        }

        /// <summary>
        /// Try to handle a slash command
        /// </summary>
        /// <returns>True if a command was handled, false otherwise</returns>
        private static async Task<bool> TryHandleCommandAsync(
            string user,
            List<ChatMessage> messages,
            HttpClient http,
            CancellationToken ct)
        {
            if (user.Equals("/exit", StringComparison.OrdinalIgnoreCase))
            {
                PermissionManager.ClearSessionPermissions();
                Environment.Exit(0);
                return true;
            }

            if (user.Equals("/test-permissions", StringComparison.OrdinalIgnoreCase))
            {
                PermissionSystemDemo.RunDemo();
                return true;
            }

            if (user.Equals("/test-mcp", StringComparison.OrdinalIgnoreCase))
            {
                await Tests.McpIntegrationTest.RunAllTests();
                return true;
            }

            if (user.StartsWith("/clear", StringComparison.OrdinalIgnoreCase))
            {
                var systemPrompt = messages[0].Content;
                messages.Clear();
                messages.Add(new ChatMessage("system", systemPrompt));
                Console.WriteLine("Conversation cleared.");
                return true;
            }

            if (user.StartsWith("/system ", StringComparison.OrdinalIgnoreCase))
            {
                var sys = user[8..].Trim();
                if (string.IsNullOrWhiteSpace(sys))
                {
                    Console.WriteLine("Usage: /system <text>");
                    return true;
                }
                messages[0] = new ChatMessage("system", sys);
                Console.WriteLine("System prompt updated.");
                return true;
            }

            if (user.StartsWith("/stream", StringComparison.OrdinalIgnoreCase))
            {
                var arg = user.Length > 7 ? user[7..].Trim() : "";
                if (string.Equals(arg, "on", StringComparison.OrdinalIgnoreCase)) AgentConfig.Config.Stream = true;
                else if (string.Equals(arg, "off", StringComparison.OrdinalIgnoreCase)) AgentConfig.Config.Stream = false;
                else
                {
                    Console.WriteLine("Usage: /stream on|off");
                    return true;
                }
                Console.WriteLine($"Streaming is now {(AgentConfig.Config.Stream ? "ON" : "OFF")}.");
                return true;
            }

            if (user.Equals("/help", StringComparison.OrdinalIgnoreCase))
            {
                ConsoleHelpers.PrintHelp();
                return true;
            }

            if (user.StartsWith("/diff", StringComparison.OrdinalIgnoreCase))
            {
                await CommandHandlers.HandleDiffCommandAsync(user, ct, null);
                return true;
            }

            if (user.StartsWith("/test", StringComparison.OrdinalIgnoreCase))
            {
                await CommandHandlers.HandleTestCommandAsync(user, ct, null);
                return true;
            }

            if (user.StartsWith("/run", StringComparison.OrdinalIgnoreCase))
            {
                await CommandHandlers.HandleRunCommandAsync(user, ct, null);
                return true;
            }

            if (user.StartsWith("/commit", StringComparison.OrdinalIgnoreCase))
            {
                await CommandHandlers.HandleCommitCommandAsync(user, ct);
                return true;
            }

            if (user.StartsWith("/push", StringComparison.OrdinalIgnoreCase))
            {
                await GitCommandHandlers.HandlePushCommandAsync(user, ct);
                return true;
            }

            if (user.StartsWith("/pull", StringComparison.OrdinalIgnoreCase))
            {
                await GitCommandHandlers.HandlePullCommandAsync(user, ct);
                return true;
            }

            if (user.Equals("/config", StringComparison.OrdinalIgnoreCase) ||
                user.StartsWith("/config ", StringComparison.OrdinalIgnoreCase))
            {
                await CommandHandlers.HandleConfigCommandAsync(user, http, ct);
                return true;
            }

            if (user.StartsWith("/set ", StringComparison.OrdinalIgnoreCase))
            {
                await CommandHandlers.HandleSetCommandAsync(user, http, ct);
                return true;
            }

            if (user.StartsWith("/rag", StringComparison.OrdinalIgnoreCase))
            {
                await RagCommandHandlers.HandleRagCommandAsync(user, ct);
                return true;
            }

            if (user.StartsWith("/mcp", StringComparison.OrdinalIgnoreCase))
            {
                await McpCommandHandlers.HandleMcpCommandAsync(user, ct);
                return true;
            }

            return false;
        }
    }
}
