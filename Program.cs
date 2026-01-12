using CodingAgent;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        private static CancellationTokenSource? _currentRequestCts;
        private static readonly object _ctsLock = new();

        public static async Task Main(string[] args)
        {
            // Set up Ctrl+C handler for graceful cancellation
            Console.CancelKeyPress += (sender, e) =>
            {
                lock (_ctsLock)
                {
                    if (_currentRequestCts != null && !_currentRequestCts.IsCancellationRequested)
                    {
                        e.Cancel = true; // Prevent immediate exit
                        _currentRequestCts.Cancel();
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine("\n[Cancelling request... Press Ctrl+C again to force exit]");
                        Console.ResetColor();
                    }
                    else
                    {
                        // No active request or already cancelled - allow exit
                        Console.WriteLine("\nExiting...");
                        PermissionManager.ClearSessionPermissions();
                    }
                }
            };
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine(AppContext.BaseDirectory);

            // Initialize logging
            AgentLogger.Initialize();
            AgentLogger.LogInfo("THUVU starting...");

            // Load configurations
            AgentConfig.LoadConfig();
            RagConfig.LoadConfig();
            McpConfig.LoadConfig();
            
            // Initialize model registry (try to load from config, fall back to AgentConfig)
            try
            {
                var configPath = AgentConfig.GetConfigPath();
                if (File.Exists(configPath))
                {
                    var json = File.ReadAllText(configPath);
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("Models", out var modelsSection))
                    {
                        ModelRegistry.LoadFromJson(modelsSection);
                    }
                    else
                    {
                        ModelRegistry.InitializeFromAgentConfig();
                    }
                }
                else
                {
                    ModelRegistry.InitializeFromAgentConfig();
                }
            }
            catch
            {
                ModelRegistry.InitializeFromAgentConfig();
            }

            // Check if TUI or Web mode is requested
            bool useTui = args.Length > 0 && args[0].Equals("--tui", StringComparison.OrdinalIgnoreCase);
            bool useWeb = args.Length > 0 && args[0].Equals("--web", StringComparison.OrdinalIgnoreCase);

            // Initialize permission manager with work directory
            PermissionManager.SetCurrentRepoPath(AgentConfig.GetWorkDirectory());

            // If web mode, start the web server
            if (useWeb)
            {
                await thuvu.Web.WebHost.RunAsync(args);
                return;
            }

            using var http = new HttpClient();
            AgentConfig.ApplyConfig(http);

            // Initialize RAG service
            RagToolImpl.Initialize(http);

            // Run health checks
            Console.WriteLine();
            ConsoleHelpers.PrintStatus("Running health checks...");
            var healthReport = await HealthCheck.RunAllChecksAsync(http, CancellationToken.None);
            HealthCheck.PrintReport(healthReport);

            if (!healthReport.CanStart)
            {
                ConsoleHelpers.PrintError("Cannot start - fix critical issues above");
                return;
            }

            // Initialize token tracker with context length
            // Priority: 1) Model-specific config, 2) AgentConfig setting, 3) API detection, 4) Default 32768
            try
            {
                // Check if current model has a specific context length configured
                var currentModel = ModelRegistry.Instance.GetModel(AgentConfig.Config.Model);
                if (currentModel?.MaxContextLength > 0)
                {
                    _currentContextLength = currentModel.MaxContextLength;
                    AgentLogger.LogInfo("Using model-specific MaxContextLength: {Length} for {Model}", 
                        _currentContextLength, currentModel.ModelId);
                }
                else if (AgentConfig.Config.MaxContextLength > 0)
                {
                    // Use configured value (user-specified for APIs that don't report context length)
                    _currentContextLength = AgentConfig.Config.MaxContextLength;
                    AgentLogger.LogInfo("Using configured MaxContextLength: {Length}", _currentContextLength);
                }
                else
                {
                    // Try to detect from API
                    var detected = await AgentLoop.GetContextLengthAsync(http, AgentConfig.Config.Model, CancellationToken.None);
                    _currentContextLength = detected ?? 32768; // Default to 32K if not detected
                    AgentLogger.LogInfo("Context length {Source}: {Length}", 
                        detected.HasValue ? "detected" : "defaulted", _currentContextLength);
                }
                TokenTracker.Instance.MaxContextLength = _currentContextLength;
            }
            catch (Exception ex)
            {
                AgentLogger.LogWarning("Could not determine context length: {Message}. Using default.", ex.Message);
                _currentContextLength = AgentConfig.Config.MaxContextLength > 0 
                    ? AgentConfig.Config.MaxContextLength 
                    : 32768;
                TokenTracker.Instance.MaxContextLength = _currentContextLength;
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

            // Original console interface - styled banner
            ConsoleHelpers.PrintHeader($"T.H.U.V.U. v{Helpers.GetCurrentGitTag()}", ConsoleColor.Cyan);
            Console.WriteLine();
            ConsoleHelpers.WithColor(ConsoleColor.DarkGray, () => Console.WriteLine("Type /exit to quit, /help for commands, --tui for Terminal UI, --web for Web UI"));
            Console.WriteLine();
            ConsoleHelpers.PrintKeyValue("Config", AgentConfig.GetConfigPath(), ConsoleColor.DarkGray, ConsoleColor.Gray);
            ConsoleHelpers.PrintKeyValue("Model", AgentConfig.Config.Model, ConsoleColor.DarkGray, ConsoleColor.Green);
            ConsoleHelpers.PrintKeyValue("Host", AgentConfig.Config.HostUrl, ConsoleColor.DarkGray, ConsoleColor.Cyan);
            ConsoleHelpers.PrintKeyValue("Work Dir", AgentConfig.GetWorkDirectory(), ConsoleColor.DarkGray, ConsoleColor.Gray);
            ConsoleHelpers.PrintKeyValue("Streaming", AgentConfig.Config.Stream ? "ON" : "OFF", ConsoleColor.DarkGray, AgentConfig.Config.Stream ? ConsoleColor.Green : ConsoleColor.Yellow);
            Console.WriteLine();

            while (true)
            {
                ConsoleHelpers.PrintPrompt();
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

                // Create cancellation token for this request
                lock (_ctsLock)
                {
                    _currentRequestCts?.Dispose();
                    _currentRequestCts = new CancellationTokenSource();
                }
                var ct = _currentRequestCts!.Token;

                // Send to LM Studio and run tool loop until final answer
                ConsoleHelpers.PrintDivider();
                ConsoleHelpers.PrintStatus($"{ConsoleHelpers.IconSend} Sending to {AgentConfig.Config.Model}... (Ctrl+C to cancel)");
                
                string? final;
                bool receivedFirstToken = false;

                // Start thinking animation task
                ConsoleHelpers.StartStreaming();
                using var thinkingCts = new CancellationTokenSource();
                var thinkingTask = Task.Run(async () =>
                {
                    try
                    {
                        while (!thinkingCts.Token.IsCancellationRequested && !receivedFirstToken)
                        {
                            ConsoleHelpers.PrintThinkingIndicator();
                            await Task.Delay(250, thinkingCts.Token);
                        }
                    }
                    catch (OperationCanceledException) { }
                }, thinkingCts.Token);

                try
                {
                    if (AgentConfig.Config.Stream)
                    {
                        final = await AgentLoop.CompleteWithToolsStreamingAsync(
                            http, AgentConfig.Config.Model, messages, tools, ct,
                            onToken: token => 
                            {
                                if (!receivedFirstToken)
                                {
                                    receivedFirstToken = true;
                                    thinkingCts.Cancel();
                                    ConsoleHelpers.ClearThinkingIndicator();
                                    ConsoleHelpers.PrintStreamingHeader(AgentConfig.Config.Model);
                                }
                                ConsoleHelpers.PrintStreamingToken(token);
                            },
                            onToolResult: ConsoleHelpers.AutoPrettyPrinterCallback,
                            onUsage: u => { ConsoleHelpers.PrintStreamingFooter(); ConsoleHelpers.PrintTokenUsage(u.PromptTokens, u.CompletionTokens, u.TotalTokens); }
                        );
                        if (!receivedFirstToken)
                        {
                            // No tokens received, cancel thinking
                            thinkingCts.Cancel();
                            ConsoleHelpers.ClearThinkingIndicator();
                        }
                    }
                    else
                    {
                        final = await AgentLoop.CompleteWithToolsAsync(
                            http, AgentConfig.Config.Model, messages, tools, ct,
                            onToolResult: ConsoleHelpers.AutoPrettyPrinterCallback
                        );
                    }
                }
                catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
                {
                    thinkingCts.Cancel();
                    ConsoleHelpers.ClearThinkingIndicator();
                    ConsoleHelpers.PrintError($"Request timed out after {AgentConfig.Config.HttpRequestTimeout} minutes.");
                    ConsoleHelpers.PrintInfo("Try increasing timeout with: /set httptimeout <minutes>");
                    continue;
                }
                catch (OperationCanceledException)
                {
                    thinkingCts.Cancel();
                    ConsoleHelpers.ClearThinkingIndicator();
                    Console.WriteLine();
                    ConsoleHelpers.PrintWarning("Request cancelled by user.");
                    // Remove the last user message since we cancelled the request
                    if (messages.Count > 1 && messages[^1].Role == "user")
                    {
                        messages.RemoveAt(messages.Count - 1);
                    }
                    continue;
                }
                catch (HttpRequestException ex)
                {
                    thinkingCts.Cancel();
                    ConsoleHelpers.ClearThinkingIndicator();
                    ConsoleHelpers.PrintError($"HTTP Error: {ex.Message}");
                    ConsoleHelpers.PrintInfo($"Is the LLM server running at {AgentConfig.Config.HostUrl}?");
                    continue;
                }
                finally
                {
                    // Clear the current request CTS
                    lock (_ctsLock)
                    {
                        _currentRequestCts?.Dispose();
                        _currentRequestCts = null;
                    }
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
                    ConsoleHelpers.PrintStatus("(no content in response)");
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

            if (user.StartsWith("/models", StringComparison.OrdinalIgnoreCase))
            {
                HandleModelsCommand(user);
                return true;
            }

            // New MVP commands
            if (user.StartsWith("/task", StringComparison.OrdinalIgnoreCase))
            {
                await HandleTaskCommandAsync(user, ct);
                return true;
            }

            if (user.StartsWith("/checkpoint", StringComparison.OrdinalIgnoreCase))
            {
                await HandleCheckpointCommandAsync(user, ct);
                return true;
            }

            if (user.StartsWith("/rollback", StringComparison.OrdinalIgnoreCase))
            {
                await HandleRollbackCommandAsync(user, ct);
                return true;
            }

            if (user.StartsWith("/tokens", StringComparison.OrdinalIgnoreCase))
            {
                HandleTokensCommand(user, messages);
                return true;
            }

            if (user.StartsWith("/summarize", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Summarizing conversation...");
                var success = await AgentLoop.SummarizeConversationAsync(
                    http, AgentConfig.Config.Model, messages, ct, 
                    s => Console.WriteLine($"  {s}"));
                if (success)
                    Console.WriteLine("✓ Conversation summarized successfully.");
                else
                    Console.WriteLine("✗ Summarization failed or not enough messages.");
                return true;
            }

            if (user.Equals("/health", StringComparison.OrdinalIgnoreCase))
            {
                var report = await HealthCheck.RunAllChecksAsync(http, ct);
                HealthCheck.PrintReport(report);
                return true;
            }

            if (user.Equals("/status", StringComparison.OrdinalIgnoreCase))
            {
                AgentSessionManager.PrintSessionStatus();
                TokenTracker.Instance.PrintStatus();
                return true;
            }
            
            if (user.StartsWith("/plan", StringComparison.OrdinalIgnoreCase))
            {
                await CommandHandlers.HandlePlanCommandAsync(user, http, ct);
                return true;
            }
            
            if (user.StartsWith("/orchestrate", StringComparison.OrdinalIgnoreCase))
            {
                await CommandHandlers.HandleOrchestrateCommandAsync(user, http, ct);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Handle /task command for session management
        /// </summary>
        private static async Task HandleTaskCommandAsync(string command, CancellationToken ct)
        {
            var args = ConsoleHelpers.TokenizeArgs(command);
            var subCommand = args.Count > 1 ? args[1].ToLowerInvariant() : "status";

            switch (subCommand)
            {
                case "start":
                    if (args.Count < 3)
                    {
                        ConsoleHelpers.PrintError("Usage: /task start <description>");
                        return;
                    }
                    var description = string.Join(" ", args.Skip(2));
                    try
                    {
                        var session = await AgentSessionManager.StartSessionAsync(description, null, ct);
                        ConsoleHelpers.PrintSuccess($"Started task: {session.TaskDescription}");
                        ConsoleHelpers.PrintKeyValue("Branch", session.BranchName, ConsoleColor.DarkGray, ConsoleColor.Green);
                        ConsoleHelpers.PrintKeyValue("Agent ID", session.AgentId, ConsoleColor.DarkGray, ConsoleColor.Cyan);
                    }
                    catch (Exception ex)
                    {
                        ConsoleHelpers.PrintError($"Failed to start task: {ex.Message}");
                    }
                    break;

                case "status":
                    AgentSessionManager.PrintSessionStatus();
                    break;

                case "complete":
                    var merge = args.Contains("--merge") || args.Contains("-m");
                    var delete = args.Contains("--delete") || args.Contains("-d");
                    if (await AgentSessionManager.CompleteSessionAsync(merge, delete, ct))
                    {
                        ConsoleHelpers.PrintSuccess("Task completed" + (merge ? " and merged" : ""));
                    }
                    else
                    {
                        ConsoleHelpers.PrintError("Failed to complete task");
                    }
                    break;

                case "abort":
                    if (await AgentSessionManager.AbortSessionAsync(true, ct))
                    {
                        ConsoleHelpers.PrintWarning("Task aborted");
                    }
                    break;

                default:
                    ConsoleHelpers.PrintHeader("Task Commands", ConsoleColor.Cyan);
                    Console.WriteLine();
                    ConsoleHelpers.PrintKeyValue("/task start <desc>", "Start new task with git branch", ConsoleColor.Green);
                    ConsoleHelpers.PrintKeyValue("/task status", "Show current task status", ConsoleColor.Green);
                    ConsoleHelpers.PrintKeyValue("/task complete [-m] [-d]", "Complete task (--merge, --delete)", ConsoleColor.Green);
                    ConsoleHelpers.PrintKeyValue("/task abort", "Abort task and discard changes", ConsoleColor.Green);
                    break;
            }
        }

        /// <summary>
        /// Handle /checkpoint command
        /// </summary>
        private static async Task HandleCheckpointCommandAsync(string command, CancellationToken ct)
        {
            var args = ConsoleHelpers.TokenizeArgs(command);
            var message = args.Count > 1 ? string.Join(" ", args.Skip(1)) : null;
            var runTests = args.Contains("--test") || args.Contains("-t");

            var checkpoint = await AgentSessionManager.CreateCheckpointAsync(message, runTests, ct);
            if (checkpoint != null)
            {
                ConsoleHelpers.PrintSuccess($"Checkpoint created: {checkpoint.Tag}");
                if (runTests)
                {
                    ConsoleHelpers.PrintKeyValue("Tests", checkpoint.TestsPassed ? "PASSED" : "FAILED",
                        ConsoleColor.DarkGray, checkpoint.TestsPassed ? ConsoleColor.Green : ConsoleColor.Red);
                }
            }
            else
            {
                ConsoleHelpers.PrintError("Failed to create checkpoint. Is there an active task?");
            }
        }

        /// <summary>
        /// Handle /rollback command
        /// </summary>
        private static async Task HandleRollbackCommandAsync(string command, CancellationToken ct)
        {
            var args = ConsoleHelpers.TokenizeArgs(command);
            var target = args.Count > 1 ? args[1] : null;

            if (await AgentSessionManager.RollbackAsync(target, ct))
            {
                ConsoleHelpers.PrintSuccess($"Rolled back to {target ?? "last checkpoint"}");
            }
            else
            {
                ConsoleHelpers.PrintError("Failed to rollback. Check if there are checkpoints available.");
            }
        }

        /// <summary>
        /// Handle /tokens command
        /// </summary>
        private static void HandleTokensCommand(string command, List<ChatMessage> messages)
        {
            var args = ConsoleHelpers.TokenizeArgs(command);
            var subCommand = args.Count > 1 ? args[1].ToLowerInvariant() : "status";

            switch (subCommand)
            {
                case "status":
                    TokenTracker.Instance.PrintStatus();
                    break;

                case "reset":
                    var systemPrompt = messages[0].Content;
                    messages.Clear();
                    messages.Add(new ChatMessage("system", systemPrompt));
                    TokenTracker.Instance.Reset();
                    ConsoleHelpers.PrintSuccess("Conversation and token count reset");
                    break;

                case "budget":
                    if (args.Count > 2 && int.TryParse(args[2], out var budget))
                    {
                        TokenTracker.Instance.MaxContextLength = budget;
                        ConsoleHelpers.PrintSuccess($"Token budget set to {budget:N0}");
                    }
                    else
                    {
                        ConsoleHelpers.PrintKeyValue("Current budget", $"{TokenTracker.Instance.MaxContextLength:N0} tokens");
                    }
                    break;

                default:
                    ConsoleHelpers.PrintHeader("Token Commands", ConsoleColor.Cyan);
                    Console.WriteLine();
                    ConsoleHelpers.PrintKeyValue("/tokens", "Show current token usage", ConsoleColor.Green);
                    ConsoleHelpers.PrintKeyValue("/tokens reset", "Reset conversation and tokens", ConsoleColor.Green);
                    ConsoleHelpers.PrintKeyValue("/tokens budget <n>", "Set max token budget", ConsoleColor.Green);
                    break;
            }
        }

        /// <summary>
        /// Handle /models command for multi-model management
        /// </summary>
        private static void HandleModelsCommand(string command)
        {
            var args = ConsoleHelpers.TokenizeArgs(command);
            var subCommand = args.Count > 1 ? args[1].ToLowerInvariant() : "list";

            switch (subCommand)
            {
                case "list":
                    ModelRegistry.Instance.PrintModels();
                    break;

                case "use":
                    if (args.Count < 3)
                    {
                        ConsoleHelpers.PrintError("Usage: /models use <model-id>");
                        return;
                    }
                    var modelId = args[2];
                    var model = ModelRegistry.Instance.GetModel(modelId);
                    if (model == null)
                    {
                        ConsoleHelpers.PrintError($"Model '{modelId}' not found");
                        return;
                    }
                    ModelRegistry.Instance.DefaultModelId = model.ModelId;
                    AgentConfig.Config.Model = model.ModelId;
                    AgentConfig.Config.HostUrl = model.HostUrl;
                    AgentConfig.Config.Stream = model.Stream;
                    ConsoleHelpers.PrintSuccess($"Now using: {model.DisplayName ?? model.ModelId}");
                    break;

                case "add":
                    ConsoleHelpers.PrintInfo("To add models, edit appsettings.json and add to the Models.Models array");
                    break;

                case "thinking":
                    if (args.Count < 3)
                    {
                        var thinking = ModelRegistry.Instance.GetThinkingModel();
                        ConsoleHelpers.PrintKeyValue("Thinking model", thinking?.DisplayName ?? "(not set)");
                    }
                    else
                    {
                        ModelRegistry.Instance.ThinkingModelId = args[2];
                        ConsoleHelpers.PrintSuccess($"Thinking model set to: {args[2]}");
                    }
                    break;

                case "coding":
                    if (args.Count < 3)
                    {
                        var coding = ModelRegistry.Instance.GetCodingModel();
                        ConsoleHelpers.PrintKeyValue("Coding model", coding?.DisplayName ?? "(not set)");
                    }
                    else
                    {
                        ModelRegistry.Instance.CodingModelId = args[2];
                        ConsoleHelpers.PrintSuccess($"Coding model set to: {args[2]}");
                    }
                    break;

                default:
                    ConsoleHelpers.PrintHeader("Models Commands", ConsoleColor.Cyan);
                    Console.WriteLine();
                    ConsoleHelpers.PrintKeyValue("/models list", "List all configured models", ConsoleColor.Green);
                    ConsoleHelpers.PrintKeyValue("/models use <id>", "Switch to a specific model", ConsoleColor.Green);
                    ConsoleHelpers.PrintKeyValue("/models thinking [id]", "Get/set thinking model", ConsoleColor.Green);
                    ConsoleHelpers.PrintKeyValue("/models coding [id]", "Get/set coding model", ConsoleColor.Green);
                    ConsoleHelpers.PrintKeyValue("/models add", "Info on adding models", ConsoleColor.Green);
                    break;
            }
        }
    }
}
