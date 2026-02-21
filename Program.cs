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
using thuvu.Web;

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
            SqliteConfig.LoadConfig();
            LspConfig.LoadConfig();
            
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

            // Parse command line arguments
            bool useTui = false;
            bool useWeb = false;
            bool useApi = false;
            bool useDesktop = false;
            bool useServer = false;    // --server: connect to agent server
            bool noServer = false;     // --no-server: force in-process mode
            string? serverUrl = null;  // --server <url>
            bool testUiAutomation = false;
            bool testProcessMgmt = false;
            bool testSqlite = false;
            string? customConfigPath = null;
            int? customPort = null;
            
            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i].ToLowerInvariant();
                if (arg == "--tui") useTui = true;
                else if (arg == "--web") useWeb = true;
                else if (arg == "--api") useApi = true;
                else if (arg == "--desktop") useDesktop = true;
                else if (arg == "--server" && i + 1 < args.Length) { useServer = true; serverUrl = args[++i]; }
                else if (arg == "--server") useServer = true;
                else if (arg == "--no-server") noServer = true;
                else if (arg == "--test-ui") testUiAutomation = true;
                else if (arg == "--test-process") testProcessMgmt = true;
                else if (arg == "--test-sqlite") testSqlite = true;
                else if (arg == "--config" && i + 1 < args.Length)
                {
                    customConfigPath = args[++i];
                }
                else if (arg == "--port" && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], out var port))
                        customPort = port;
                }
            }
            
            // If custom config path specified, set environment variable and reload
            if (!string.IsNullOrEmpty(customConfigPath) && File.Exists(customConfigPath))
            {
                AgentLogger.LogInfo("Loading custom config from: {Path}", customConfigPath);
                Environment.SetEnvironmentVariable("LM_AGENT_CONFIG", customConfigPath);
                // Reload all configs with new environment variable
                AgentConfig.LoadConfig();
                RagConfig.LoadConfig();
                McpConfig.LoadConfig();
                SqliteConfig.LoadConfig();
                LspConfig.LoadConfig();
            }
            
            // Load agent API config (always load this)
            AgentApiConfig.LoadConfig();
            
            // Apply command line overrides
            if (useApi)
            {
                AgentApiConfig.Instance.Enabled = true;
            }
            if (customPort.HasValue)
            {
                AgentApiConfig.Instance.Port = customPort.Value;
            }

            // Initialize permission manager with work directory
            PermissionManager.SetCurrentRepoPath(AgentConfig.GetWorkDirectory());;
            
            // If UI automation test mode, run tests and exit
            if (testUiAutomation)
            {
                await thuvu.Tests.UIAutomationTest.RunTests();
                return;
            }
            
            // If process management test mode, run tests and exit
            if (testProcessMgmt)
            {
                await thuvu.Tests.ProcessManagementTest.RunAllTestsAsync();
                return;
            }
            
            // If SQLite test mode, run tests and exit
            if (testSqlite)
            {
                var result = await thuvu.Tests.SqliteTest.RunTests();
                Environment.Exit(result);
                return;
            }

            // If web mode (without API agent mode), start the web server standalone
            if (useWeb && !useApi)
            {
                // Web + server mode: web UI proxies to remote agent server
                if (useServer && !noServer)
                {
                    await thuvu.Web.WebHost.RunAsync(args, serverUrl: serverUrl);
                }
                else
                {
                    await thuvu.Web.WebHost.RunAsync(args);
                }
                return;
            }

            // Client/server mode: connect to a running agent server (or auto-spawn one)
            // Skip all local initialization — the server handles everything
            if (useServer && !noServer && !useTui)
            {
                await RunCliClientModeAsync(serverUrl);
                return;
            }

            using var http = new HttpClient();
            AgentConfig.ApplyConfig(http);

            // Initialize RAG service
            RagToolImpl.Initialize(http);;

            // Initialize LSP service (lazy — servers spawn on first file access)
            if (LspConfig.Config.Enabled)
            {
                var lspService = thuvu.Services.Lsp.LspService.Initialize(AgentConfig.GetWorkDirectory());
                RegisterLspServerFactories(lspService);
            }

            // Run health checks (skip in API mode and TUI client mode)
            if (!useApi && !(useTui && useServer && !noServer))
            {
                Console.WriteLine();
                ConsoleHelpers.PrintStatus("Running health checks...");
                var healthReport = await HealthCheck.RunAllChecksAsync(http, CancellationToken.None);
                HealthCheck.PrintReport(healthReport);

                if (!healthReport.CanStart)
                {
                    ConsoleHelpers.PrintError("Cannot start - fix critical issues above");
                    return;
                }
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
                
                // Store detected context length back to model config
                var modelEndpoint = ModelRegistry.Instance.GetModel(AgentConfig.Config.Model);
                if (modelEndpoint != null && modelEndpoint.MaxContextLength == 0 && _currentContextLength > 0)
                {
                    modelEndpoint.MaxContextLength = _currentContextLength;
                    AgentLogger.LogInfo("Stored detected context length {Length} to model config", _currentContextLength);
                }
            }
            catch (Exception ex)
            {
                AgentLogger.LogWarning("Could not determine context length: {Message}. Using default.", ex.Message);
                _currentContextLength = AgentConfig.Config.MaxContextLength > 0 
                    ? AgentConfig.Config.MaxContextLength 
                    : 32768;
                TokenTracker.Instance.MaxContextLength = _currentContextLength;
            }

            // Conversation state - use appropriate system prompt based on model
            var messages = new List<ChatMessage>
            {
                new("system", SystemPromptManager.Instance.GetCurrentSystemPrompt(McpConfig.Instance.McpModeActive))
            };

            // Tools you expose to the model
            var tools = BuildTools.GetToolsForSession();

            // If API mode is enabled, start the web server with agent API and set up job processor
            if (useApi)
            {
                // Initialize the job service
                await AgentJobService.Instance.InitializeAsync();

                // Initialize conversation service for client/server architecture
                ConversationService.Initialize();
                await ConversationService.Instance.RestoreFromDatabaseAsync();
                
                // Set up the streaming job processor callback
                thuvu.Web.AgentJobProcessor.SetStreamingCallback(async (jobId, prompt, emit, ct, modelOverride, systemPromptOverride) =>
                {
                    // Resolve model: use override from client, or fall back to default
                    var modelId = modelOverride ?? AgentConfig.Config.Model;
                    var modelEndpoint = ModelRegistry.Instance.GetModel(modelId);
                    var effectiveHttp = http;
                    
                    // If a different model is requested, create a dedicated HttpClient for it
                    if (modelEndpoint != null && modelOverride != null)
                    {
                        effectiveHttp = modelEndpoint.CreateHttpClient();
                        modelId = modelEndpoint.ModelId;
                    }

                    var systemPrompt = systemPromptOverride 
                        ?? SystemPromptManager.Instance.GetCurrentSystemPrompt(McpConfig.Instance.McpModeActive);

                    var jobMessages = new List<ChatMessage>
                    {
                        new("system", systemPrompt),
                        new("user", prompt)
                    };
                    
                    await AgentJobService.Instance.AddJournalEntryAsync("Processing prompt...", ct);
                    
                    try
                    {
                        string? result;
                        if (AgentConfig.Config.Stream)
                        {
                            result = await AgentLoop.CompleteWithToolsStreamingAsync(
                                effectiveHttp, modelId, jobMessages, tools, ct,
                                onToken: token => emit(AgentStreamEvent.Token(token)),
                                onToolResult: (name, json) => 
                                {
                                    emit(AgentStreamEvent.ToolComplete(name, "", json, 0));
                                    AgentJobService.Instance.AddJournalEntryAsync($"Tool: {name}").Wait();
                                },
                                onUsage: usage => emit(AgentStreamEvent.UsageInfo(usage))
                            );
                        }
                        else
                        {
                            result = await AgentLoop.CompleteWithToolsAsync(
                                effectiveHttp, modelId, jobMessages, tools, ct,
                                onToolResult: (name, json) =>
                                {
                                    emit(AgentStreamEvent.ToolComplete(name, "", json, 0));
                                    AgentJobService.Instance.AddJournalEntryAsync($"Tool: {name}").Wait();
                                }
                            );
                        }
                        
                        await AgentJobService.Instance.AddJournalEntryAsync("Completed successfully", ct);
                        return result ?? "No response generated";
                    }
                    catch (OperationCanceledException)
                    {
                        await AgentJobService.Instance.AddJournalEntryAsync("Cancelled by user", ct);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        await AgentJobService.Instance.AddJournalEntryAsync($"Error: {ex.Message}", ct);
                        throw;
                    }
                });
                
                // Set up conversation message processor callback
                thuvu.Web.ConversationApiEndpoints.ProcessMessageCallback = async (convId, prompt, history, emit, ct, modelOverride, systemPromptOverride, workDir) =>
                {
                    var modelId = modelOverride ?? AgentConfig.Config.Model;
                    var modelEndpoint = ModelRegistry.Instance.GetModel(modelId);
                    var effectiveHttp = http;
                    
                    if (modelEndpoint != null && modelOverride != null)
                    {
                        effectiveHttp = modelEndpoint.CreateHttpClient();
                        modelId = modelEndpoint.ModelId;
                    }

                    var systemPrompt = systemPromptOverride 
                        ?? SystemPromptManager.Instance.GetCurrentSystemPrompt(McpConfig.Instance.McpModeActive);

                    // Build message list from conversation history
                    var convMessages = new List<ChatMessage> { new("system", systemPrompt) };
                    foreach (var msg in history)
                    {
                        convMessages.Add(new ChatMessage(msg.Role, msg.Content));
                    }

                    string? result;
                    if (AgentConfig.Config.Stream)
                    {
                        result = await AgentLoop.CompleteWithToolsStreamingAsync(
                            effectiveHttp, modelId, convMessages, tools, ct,
                            onToken: token => emit(AgentStreamEvent.Token(token)),
                            onToolResult: (name, json) => emit(AgentStreamEvent.ToolComplete(name, "", json, 0)),
                            onUsage: usage => emit(AgentStreamEvent.UsageInfo(usage))
                        );
                    }
                    else
                    {
                        result = await AgentLoop.CompleteWithToolsAsync(
                            effectiveHttp, modelId, convMessages, tools, ct,
                            onToolResult: (name, json) => emit(AgentStreamEvent.ToolComplete(name, "", json, 0))
                        );
                    }
                    
                    return result ?? "No response generated";
                };

                // Set up slash command processor callback
                thuvu.Web.ConversationApiEndpoints.ProcessCommandCallback = async (convId, command, ct) =>
                {
                    try
                    {
                        // Use the existing command dispatch infrastructure
                        var output = new System.IO.StringWriter();
                        var originalOut = Console.Out;
                        Console.SetOut(output);
                        
                        try
                        {
                            var handled = await TryHandleCommandAsync(command, messages, http, ct);
                            Console.SetOut(originalOut);
                            
                            if (handled)
                                return CommandResult.Ok(output.ToString().TrimEnd());
                            else
                                return CommandResult.Fail($"Unknown command: {command}");
                        }
                        finally
                        {
                            Console.SetOut(originalOut);
                        }
                    }
                    catch (Exception ex)
                    {
                        return CommandResult.Fail(ex.Message);
                    }
                };
                
                // Start web server in background
                var webCts = new CancellationTokenSource();
                var webTask = Task.Run(async () =>
                {
                    await thuvu.Web.WebHost.RunAsync(args, webCts.Token);
                });
                
                ConsoleHelpers.PrintHeader($"T.H.U.V.U. Agent API v{Helpers.GetCurrentGitTag()}", ConsoleColor.Cyan);
                Console.WriteLine();
                ConsoleHelpers.PrintKeyValue("Agent Name", AgentApiConfig.Instance.AgentName, ConsoleColor.DarkGray, ConsoleColor.Green);
                ConsoleHelpers.PrintKeyValue("API Port", $"http://localhost:{AgentApiConfig.Instance.Port}", ConsoleColor.DarkGray, ConsoleColor.Cyan);
                ConsoleHelpers.PrintKeyValue("Model", AgentConfig.Config.Model, ConsoleColor.DarkGray, ConsoleColor.Green);
                ConsoleHelpers.PrintKeyValue("Work Dir", AgentConfig.GetWorkDirectory(), ConsoleColor.DarkGray, ConsoleColor.Gray);
                Console.WriteLine();
                ConsoleHelpers.WithColor(ConsoleColor.DarkGray, () => Console.WriteLine("Agent is listening for jobs. Press Ctrl+C to stop."));
                Console.WriteLine();
                
                // Wait for web server (blocks until Ctrl+C)
                try
                {
                    await webTask;
                }
                catch (OperationCanceledException) { }
                
                return;
            }

            if (useTui)
            {
                if (useServer && !noServer)
                {
                    // TUI in client/server mode
                    var client = await ConnectOrSpawnServerAsync(serverUrl);
                    if (client == null) return;
                    await client.CreateConversationAsync(model: AgentConfig.Config.Model);
                    var tuiClient = new TuiInterface(client, tools, messages);
                    tuiClient.Run();
                    client.Dispose();
                }
                else
                {
                    // TUI in direct/in-process mode
                    var tuiInterface = new TuiInterface(http, tools, messages);
                    tuiInterface.Run();
                }
                return;
            }

            if (useDesktop)
            {
                // Launch Avalonia Desktop UI
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("Launching T.H.U.V.U. Desktop...");
                Console.ResetColor();
                Console.WriteLine("Note: The desktop project must be run directly via thuvu.Desktop executable.");
                Console.WriteLine("Run: dotnet run --project thuvu.Desktop");
                return;
            }

            // Original console interface - styled banner
            ConsoleHelpers.PrintHeader($"T.H.U.V.U. v{Helpers.GetCurrentGitTag()}", ConsoleColor.Cyan);
            Console.WriteLine();
            ConsoleHelpers.WithColor(ConsoleColor.DarkGray, () => Console.WriteLine("Type /exit to quit, /help for commands, --tui for Terminal UI, --web for Web UI, --desktop for Desktop UI"));
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
                                    // End reasoning section if we were in it before starting content
                                    ConsoleHelpers.EndReasoningSection();
                                    ConsoleHelpers.PrintStreamingHeader(AgentConfig.Config.Model);
                                }
                                ConsoleHelpers.PrintStreamingToken(token);
                            },
                            onToolResult: ConsoleHelpers.AutoPrettyPrinterCallback,
                            onUsage: u => { ConsoleHelpers.PrintStreamingFooter(); ConsoleHelpers.PrintTokenUsage(u.PromptTokens, u.CompletionTokens, u.TotalTokens); },
                            onReasoningToken: token =>
                            {
                                if (!receivedFirstToken)
                                {
                                    receivedFirstToken = true;
                                    thinkingCts.Cancel();
                                    ConsoleHelpers.ClearThinkingIndicator();
                                }
                                ConsoleHelpers.PrintReasoningToken(token);
                            }
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
                    messages.Add(ChatMessage.CreateAssistant(final));

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
                // Shutdown LSP servers gracefully
                try { thuvu.Services.Lsp.LspService.Instance.Dispose(); } catch { }
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
            
            if (user.StartsWith("/prompt", StringComparison.OrdinalIgnoreCase))
            {
                await CommandHandlers.HandlePromptCommandAsync(user, messages);
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

            if (user.StartsWith("/browser", StringComparison.OrdinalIgnoreCase))
            {
                await HandleBrowserCommandAsync(user, ct);
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
                var (success, _) = await AgentLoop.SummarizeConversationAsync(
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

            if (user.StartsWith("/lsp", StringComparison.OrdinalIgnoreCase))
            {
                HandleLspCommand(user);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Handle /browser command for web browsing
        /// </summary>
        private static async Task HandleBrowserCommandAsync(string command, CancellationToken ct)
        {
            var args = ConsoleHelpers.TokenizeArgs(command);
            var subCommand = args.Count > 1 ? args[1].ToLowerInvariant() : "help";

            switch (subCommand)
            {
                case "install":
                    Console.WriteLine("Installing Playwright browsers...");
                    var installResult = await thuvu.Tools.BrowserToolImpl.InstallBrowsersAsync();
                    Console.WriteLine(installResult);
                    break;

                case "open":
                    if (args.Count < 3)
                    {
                        Console.WriteLine("Usage: /browser open <url>");
                        return;
                    }
                    var url = args[2];
                    Console.WriteLine($"Navigating to {url}...");
                    var browseResult = await thuvu.Tools.BrowserToolImpl.BrowseUrlAsync(
                        System.Text.Json.JsonSerializer.Serialize(new { url, extract_text = true }), ct);
                    
                    // Parse and display result nicely
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(browseResult);
                        var root = doc.RootElement;
                        
                        if (root.TryGetProperty("error", out var err))
                        {
                            ConsoleHelpers.WithColor(ConsoleColor.Red, () => Console.WriteLine($"Error: {err.GetString()}"));
                        }
                        else
                        {
                            var title = root.TryGetProperty("title", out var t) ? t.GetString() : "Untitled";
                            var pageUrl = root.TryGetProperty("url", out var u) ? u.GetString() : url;
                            
                            Console.WriteLine();
                            ConsoleHelpers.WithColor(ConsoleColor.Cyan, () => Console.WriteLine($"Title: {title}"));
                            ConsoleHelpers.WithColor(ConsoleColor.DarkGray, () => Console.WriteLine($"URL: {pageUrl}"));
                            Console.WriteLine();
                            
                            if (root.TryGetProperty("text", out var text))
                            {
                                var content = text.GetString() ?? "";
                                // Truncate for console display
                                if (content.Length > 2000)
                                    content = content.Substring(0, 2000) + "\n... [truncated]";
                                Console.WriteLine(content);
                            }
                        }
                    }
                    catch
                    {
                        Console.WriteLine(browseResult);
                    }
                    break;

                case "close":
                    var closeResult = await thuvu.Tools.BrowserToolImpl.CloseBrowserAsync();
                    Console.WriteLine("Browser closed.");
                    break;

                default:
                    Console.WriteLine("Browser commands:");
                    Console.WriteLine("  /browser install    - Install Playwright browsers (required first time)");
                    Console.WriteLine("  /browser open <url> - Navigate to URL and show content");
                    Console.WriteLine("  /browser close      - Close the browser");
                    Console.WriteLine();
                    Console.WriteLine("The LLM can also use browser tools directly:");
                    Console.WriteLine("  browser_navigate, browser_click, browser_type,");
                    Console.WriteLine("  browser_get_elements, browser_screenshot, browser_script");
                    break;
            }
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

                case "info":
                    {
                        string modelId;
                        if (args.Count >= 3)
                        {
                            modelId = args[2];
                        }
                        else
                        {
                            modelId = AgentConfig.Config.Model;
                        }
                        
                        var model = ModelRegistry.Instance.GetModel(modelId);
                        if (model == null)
                        {
                            ConsoleHelpers.PrintError($"Model '{modelId}' not found");
                            return;
                        }
                        
                        ConsoleHelpers.PrintDivider($"Model Info: {model.DisplayName ?? model.ModelId}", ConsoleColor.Cyan);
                        ConsoleHelpers.PrintKeyValue("Model ID", model.ModelId);
                        ConsoleHelpers.PrintKeyValue("Host URL", model.HostUrl);
                        ConsoleHelpers.PrintKeyValue("Type", model.IsLocal ? "Local" : "Remote");
                        ConsoleHelpers.PrintKeyValue("Streaming", model.Stream ? "Enabled" : "Disabled");
                        ConsoleHelpers.PrintKeyValue("Tool Support", model.SupportsTools ? "Yes" : "No");
                        ConsoleHelpers.PrintKeyValue("Vision Support", model.SupportsVision ? "Yes" : "No");
                        ConsoleHelpers.PrintKeyValue("Thinking Model", model.IsThinkingModel ? "Yes" : "No");
                        
                        if (model.MaxContextLength > 0)
                        {
                            ConsoleHelpers.PrintKeyValue("Context Length", $"{model.MaxContextLength:N0} tokens");
                            
                            // Also show current usage if this is the active model
                            if (model.ModelId == AgentConfig.Config.Model)
                            {
                                var tracker = TokenTracker.Instance;
                                ConsoleHelpers.PrintKeyValue("Context Usage", $"{tracker.TotalTokens:N0} / {tracker.MaxContextLength:N0} ({tracker.UsagePercent:P1})");
                            }
                        }
                        else
                        {
                            ConsoleHelpers.PrintKeyValue("Context Length", "Unknown (will be detected on first use)");
                        }
                        
                        if (model.MaxOutputTokens > 0)
                            ConsoleHelpers.PrintKeyValue("Max Output", $"{model.MaxOutputTokens:N0} tokens");
                        
                        ConsoleHelpers.PrintKeyValue("Temperature", model.Temperature.ToString("F1"));
                        ConsoleHelpers.PrintKeyValue("Purposes", string.Join(", ", model.Purposes));
                        ConsoleHelpers.PrintKeyValue("Priority", model.Priority.ToString());
                        ConsoleHelpers.PrintKeyValue("Enabled", model.Enabled ? "Yes" : "No");
                        Console.WriteLine();
                    }
                    break;

                case "use":
                    {
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
                    }
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
                    ConsoleHelpers.PrintKeyValue("/models info [id]", "Show detailed info about current or specified model", ConsoleColor.Green);
                    ConsoleHelpers.PrintKeyValue("/models use <id>", "Switch to a specific model", ConsoleColor.Green);
                    ConsoleHelpers.PrintKeyValue("/models thinking [id]", "Get/set thinking model", ConsoleColor.Green);
                    ConsoleHelpers.PrintKeyValue("/models coding [id]", "Get/set coding model", ConsoleColor.Green);
                    ConsoleHelpers.PrintKeyValue("/models add", "Info on adding models", ConsoleColor.Green);
                    break;
            }
        }
        
        private static void HandleLspCommand(string command)
        {
            var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var subCommand = parts.Length > 1 ? parts[1].ToLowerInvariant() : "status";
            
            switch (subCommand)
            {
                case "status":
                    if (!LspConfig.Config.Enabled)
                    {
                        ConsoleHelpers.PrintInfo("[LSP] Disabled in configuration");
                        return;
                    }
                    try
                    {
                        var servers = thuvu.Services.Lsp.LspService.Instance.GetStatus();
                        if (servers.Count == 0)
                        {
                            ConsoleHelpers.PrintInfo("[LSP] No servers running (will start on first file access)");
                        }
                        else
                        {
                            ConsoleHelpers.PrintHeader("LSP Servers", ConsoleColor.Cyan);
                            foreach (var (id, ready) in servers)
                            {
                                var status = ready ? "✓ Ready" : "✗ Not Ready";
                                var color = ready ? ConsoleColor.Green : ConsoleColor.Yellow;
                                ConsoleHelpers.PrintKeyValue(id, status, ConsoleColor.White, color);
                            }
                        }
                        ConsoleHelpers.PrintKeyValue("Auto-Diagnostics", LspConfig.Config.AutoDiagnostics ? "On" : "Off", ConsoleColor.DarkGray);
                        ConsoleHelpers.PrintKeyValue("Timeout", $"{LspConfig.Config.DiagnosticsTimeoutMs}ms", ConsoleColor.DarkGray);
                    }
                    catch
                    {
                        ConsoleHelpers.PrintInfo("[LSP] Service not initialized");
                    }
                    break;
                    
                case "restart":
                    try
                    {
                        thuvu.Services.Lsp.LspService.Instance.Dispose();
                        var svc = thuvu.Services.Lsp.LspService.Initialize(AgentConfig.GetWorkDirectory());
                        RegisterLspServerFactories(svc);
                        ConsoleHelpers.PrintSuccess("[LSP] Service restarted");
                    }
                    catch (Exception ex)
                    {
                        ConsoleHelpers.PrintError($"[LSP] Restart failed: {ex.Message}");
                    }
                    break;
                    
                case "diagnostics":
                    var file = parts.Length > 2 ? parts[2] : null;
                    if (file == null)
                    {
                        ConsoleHelpers.PrintInfo("Usage: /lsp diagnostics <file>");
                        return;
                    }
                    try
                    {
                        var fullPath = Path.GetFullPath(file, AgentConfig.GetWorkDirectory());
                        var summary = thuvu.Services.Lsp.LspService.Instance.GetDiagnosticsSummaryAsync(fullPath).GetAwaiter().GetResult();
                        Console.WriteLine(summary ?? "No diagnostics (clean)");
                    }
                    catch (Exception ex)
                    {
                        ConsoleHelpers.PrintError($"[LSP] {ex.Message}");
                    }
                    break;
                    
                default:
                    ConsoleHelpers.PrintHeader("LSP Commands", ConsoleColor.Cyan);
                    ConsoleHelpers.PrintKeyValue("/lsp status", "Show LSP server status", ConsoleColor.Green);
                    ConsoleHelpers.PrintKeyValue("/lsp restart", "Restart all LSP servers", ConsoleColor.Green);
                    ConsoleHelpers.PrintKeyValue("/lsp diagnostics <file>", "Show diagnostics for a file", ConsoleColor.Green);
                    break;
            }
        }
        
        private static void RegisterLspServerFactories(thuvu.Services.Lsp.LspService lspService)
        {
            var omnisharpConfig = LspConfig.Config.GetServerConfig("omnisharp");
            if (omnisharpConfig?.Disabled != true)
            {
                lspService.RegisterServerFactory(ext =>
                {
                    if (ext is ".cs" or ".csx")
                    {
                        var server = new thuvu.Services.Lsp.OmniSharpServer();
                        // Set explicit path if configured
                        if (!string.IsNullOrEmpty(omnisharpConfig?.Path))
                            server.ExePath = omnisharpConfig.Path;
                        // Try auto-download path
                        else
                        {
                            var downloadedPath = thuvu.Services.Lsp.LspDownloadService
                                .EnsureOmniSharpAsync(omnisharpConfig).GetAwaiter().GetResult();
                            if (downloadedPath != null)
                                server.ExePath = downloadedPath;
                        }
                        return server;
                    }
                    return null;
                });
            }
        }

        /// <summary>
        /// Connect to an existing server or auto-spawn one. Reusable by CLI and TUI client modes.
        /// </summary>
        private static async Task<thuvu.Services.AgentClient?> ConnectOrSpawnServerAsync(string? serverUrl)
        {
            thuvu.Services.AgentClient? client = null;

            if (!string.IsNullOrEmpty(serverUrl))
            {
                ConsoleHelpers.PrintStatus($"Connecting to {serverUrl}...");
                client = new thuvu.Services.AgentClient(serverUrl);
                if (!await client.ConnectAsync())
                {
                    ConsoleHelpers.PrintError($"Cannot connect to server at {serverUrl}");
                    return null;
                }
                ConsoleHelpers.PrintSuccess($"Connected to {serverUrl}");
            }
            else
            {
                ConsoleHelpers.PrintStatus("Looking for running agent server...");
                var serverInfo = await thuvu.Services.AgentServerLocator.FindRunningServerAsync();

                if (serverInfo != null)
                {
                    ConsoleHelpers.PrintSuccess($"Found server at {serverInfo.Url} (PID {serverInfo.Pid})");
                    client = new thuvu.Services.AgentClient(serverInfo.Url, serverInfo.Token);
                    if (!await client.ConnectAsync())
                    {
                        ConsoleHelpers.PrintError("Server found but failed to connect");
                        return null;
                    }
                }
                else
                {
                    ConsoleHelpers.PrintStatus("No server found. Starting agent server...");
                    var spawned = await thuvu.Services.AgentServerLocator.SpawnServerAsync();
                    if (spawned == null)
                    {
                        ConsoleHelpers.PrintError("Failed to start agent server. Use --no-server for in-process mode.");
                        return null;
                    }
                    ConsoleHelpers.PrintSuccess($"Server started at {spawned.Url} (PID {spawned.Pid})");
                    client = new thuvu.Services.AgentClient(spawned.Url, spawned.Token);
                    if (!await client.ConnectAsync())
                    {
                        ConsoleHelpers.PrintError("Server started but failed to connect");
                        return null;
                    }
                }
            }

            return client;
        }

        /// <summary>
        /// Run the CLI in client mode — connects to a running agent server via HTTP+SSE.
        /// Falls back to auto-spawning a server if none is found.
        /// </summary>
        private static async Task RunCliClientModeAsync(string? serverUrl)
        {
            ConsoleHelpers.PrintHeader($"T.H.U.V.U. v{Helpers.GetCurrentGitTag()} [Client Mode]", ConsoleColor.Cyan);
            Console.WriteLine();

            var client = await ConnectOrSpawnServerAsync(serverUrl);
            if (client == null) return;

            // Create a conversation
            var convId = await client.CreateConversationAsync(model: AgentConfig.Config.Model);
            if (convId == null)
            {
                ConsoleHelpers.PrintError("Failed to create conversation");
                return;
            }

            ConsoleHelpers.PrintKeyValue("Server", client.BaseUrl, ConsoleColor.DarkGray, ConsoleColor.Cyan);
            ConsoleHelpers.PrintKeyValue("Model", client.EffectiveModel, ConsoleColor.DarkGray, ConsoleColor.Green);
            ConsoleHelpers.PrintKeyValue("Conversation", convId, ConsoleColor.DarkGray, ConsoleColor.Gray);
            ConsoleHelpers.PrintKeyValue("Work Dir", AgentConfig.GetWorkDirectory(), ConsoleColor.DarkGray, ConsoleColor.Gray);
            Console.WriteLine();
            ConsoleHelpers.WithColor(ConsoleColor.DarkGray, () =>
                Console.WriteLine("Type /exit to quit, /help for commands. All processing happens on the server."));
            Console.WriteLine();

            // Wire up events for console output
            bool receivedFirstToken = false;
            CancellationTokenSource? thinkingCts = null;

            client.OnToken += token =>
            {
                if (!receivedFirstToken)
                {
                    receivedFirstToken = true;
                    thinkingCts?.Cancel();
                    ConsoleHelpers.ClearThinkingIndicator();
                    ConsoleHelpers.EndReasoningSection();
                    ConsoleHelpers.PrintStreamingHeader(client.EffectiveModel);
                }
                ConsoleHelpers.PrintStreamingToken(token);
            };

            client.OnReasoningToken += token =>
            {
                if (!receivedFirstToken)
                {
                    receivedFirstToken = true;
                    thinkingCts?.Cancel();
                    ConsoleHelpers.ClearThinkingIndicator();
                }
                ConsoleHelpers.PrintReasoningToken(token);
            };

            client.OnToolCall += (name, args) =>
            {
                if (!receivedFirstToken)
                {
                    receivedFirstToken = true;
                    thinkingCts?.Cancel();
                    ConsoleHelpers.ClearThinkingIndicator();
                }
            };

            client.OnToolComplete += (name, args, result, elapsed) =>
            {
                ConsoleHelpers.AutoPrettyPrinterCallback(name, result);
            };

            client.OnUsage += usage =>
            {
                ConsoleHelpers.PrintStreamingFooter();
                ConsoleHelpers.PrintTokenUsage(usage.PromptTokens, usage.CompletionTokens, usage.TotalTokens);
            };

            client.OnComplete += () =>
            {
                if (!receivedFirstToken)
                {
                    thinkingCts?.Cancel();
                    ConsoleHelpers.ClearThinkingIndicator();
                }
            };

            client.OnError += error =>
            {
                thinkingCts?.Cancel();
                ConsoleHelpers.ClearThinkingIndicator();
                ConsoleHelpers.PrintError(error);
            };

            client.OnPermissionRequest += async (id, tool, permArgs, desc) =>
            {
                Console.WriteLine();
                ConsoleHelpers.PrintWarning($"Permission required: {tool}");
                if (!string.IsNullOrEmpty(desc))
                    ConsoleHelpers.PrintInfo(desc);
                Console.Write("Allow? [y/N] ");
                var response = Console.ReadLine();
                return response?.Trim().ToLowerInvariant() is "y" or "yes";
            };

            // Main input loop
            while (true)
            {
                ConsoleHelpers.PrintPrompt();
                var user = Console.ReadLine();
                if (user == null) continue;

                if (user.Equals("/exit", StringComparison.OrdinalIgnoreCase))
                    break;

                if (string.IsNullOrWhiteSpace(user)) continue;

                // Slash commands go through the server
                if (user.StartsWith("/"))
                {
                    var (success, output, error) = await client.SendCommandAsync(user);
                    if (!string.IsNullOrEmpty(output))
                        Console.WriteLine(output);
                    if (!string.IsNullOrEmpty(error))
                        ConsoleHelpers.PrintError(error);
                    continue;
                }

                // Regular message — stream via SSE
                receivedFirstToken = false;
                ConsoleHelpers.PrintDivider();
                ConsoleHelpers.PrintStatus($"{ConsoleHelpers.IconSend} Sending to server... (Ctrl+C to cancel)");
                ConsoleHelpers.StartStreaming();

                thinkingCts = new CancellationTokenSource();
                _ = Task.Run(async () =>
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

                await client.SendMessageAsync(user);
                Console.WriteLine();
            }

            client.Dispose();
        }
    }
}
