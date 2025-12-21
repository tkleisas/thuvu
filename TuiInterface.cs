using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Terminal.Gui;
using thuvu.Models;
using thuvu.Tui;
using CodingAgent;
using thuvu.Tools;
using TgAttribute = Terminal.Gui.Attribute;

namespace thuvu
{
    /// <summary>
    /// Terminal.GUI v2 based interface for THUVU
    /// </summary>
    public class TuiInterface
    {
        private readonly HttpClient _http;
        private readonly List<Tool> _tools;
        private List<ChatMessage> _messages;
        private readonly CancellationTokenSource _appCancellationTokenSource = new();
        private CancellationTokenSource? _currentRequestCts;
        private CancellationTokenSource? _thinkingAnimationCts;
        private bool _isProcessing = false;

        // UI Components
        private Label? _statusLabel;
        private Label? _workLabel;
        private Label? _commandLabel;
        private TextView? _actionView;
        private TextView? _commandField;
        private Button? _sendButton;
        private Button? _cancelButton;
        
        // Refactored components
        private TuiAutocomplete? _autocomplete;
        private TuiOrchestrationView? _orchestrationView;
        private Toplevel? _top;
        
        // Orchestration state - uses refactored TuiOrchestrationView
        private bool _orchestrationMode = false;
        
        public TuiInterface(HttpClient http, List<Tool> tools, List<ChatMessage> initialMessages)
        {
            _http = http;
            _tools = tools;
            _messages = initialMessages;
        }
        
        public void Run()
        {
            Application.Init();
            
            // Set up TUI permission prompt handler using refactored component
            PermissionManager.CustomPermissionPrompt = (toolName, argsJson) =>
            {
                var result = TuiPermissionDialog.Show(toolName, argsJson, action =>
                {
                    // Update action view with permission result
                    var timestamp = DateTime.Now.ToString("HH:mm:ss");
                    var icon = action == "Denied" ? "[DENY]" : "[PERMIT]";
                    AppendToActionView($"  [{timestamp}] {icon} {toolName}\n");
                });
                return result;
            };
            
            // Set up Ctrl+C handler as fallback
            Console.CancelKeyPress += OnConsoleCancelKeyPress;
            
            try
            {
                _top = new Toplevel();
                _autocomplete = new TuiAutocomplete();
                _orchestrationView = new TuiOrchestrationView(_top);
                SetupUi(_top);
                Application.Run(_top);
            }
            finally
            {
                Console.CancelKeyPress -= OnConsoleCancelKeyPress;
                PermissionManager.CustomPermissionPrompt = null;
                Application.Shutdown();
                _appCancellationTokenSource.Cancel();
                _orchestrationView?.Dispose();
            }
        }
        
        private void OnConsoleCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
        {
            if (_isProcessing && _currentRequestCts != null && !_currentRequestCts.IsCancellationRequested)
            {
                e.Cancel = true; // Prevent immediate exit
                _currentRequestCts.Cancel();
                Application.Invoke(() =>
                {
                    AppendActionText("[Ctrl+C: Cancelling...]", true);
                    Application.Wakeup();
                });
            }
        }
        
        private void SetupUi(Toplevel top)
        {
            // Status area (top)
            _statusLabel = new Label
            {
                X = 0,
                Y = 0,
                Height = 1,
                Width = Dim.Fill(),
                Text = GetStatusText(),
                ColorScheme = TuiStyles.StatusBar
            };
            
            // Action area (middle) - scrollable text view  
            _actionView = new TextView
            {
                X = 0,
                Y = 2,
                Width = Dim.Fill(),
                Height = Dim.Fill() - 7,
                ReadOnly = true,
                WordWrap = true,
                ColorScheme = TuiStyles.ActionView,
                Text = TuiStyles.Banner + TuiStyles.WelcomeMessage
            };

            // Command area labels
            _commandLabel = new Label
            {
                X = 0,
                Y = Pos.Bottom(_actionView),
                Text = "Command (Ctrl+Enter): ",
                ColorScheme = TuiStyles.CommandLabel
            };
            
            _workLabel = new Label
            {
                X = Pos.Right(_commandLabel),
                Y = Pos.Bottom(_actionView),
                Width = 30,
                Height = 1,
                Text = " ",
                ColorScheme = TuiStyles.WorkLabel
            };
            
            // Multi-line command input
            _commandField = new TextView
            {
                X = 0,
                Y = Pos.Bottom(_actionView) + 1,
                Width = Dim.Fill() - 12,
                Height = 4,
                WordWrap = true,
                ColorScheme = TuiStyles.CommandField
            };

            _sendButton = new Button
            {
                X = Pos.Right(_commandField) + 1,
                Y = Pos.Bottom(_actionView) + 1,
                Text = "_Send",
                IsDefault = false
            };

            _cancelButton = new Button
            {
                X = Pos.Right(_commandField) + 1,
                Y = Pos.Bottom(_actionView) + 2,
                Text = "_Cancel",
                Visible = false
            };
            
            // Setup autocomplete selection handler
            _autocomplete!.List.OpenSelectedItem += OnAutocompleteSelected;

            // Event handlers
            _sendButton.Accepting += (s, e) => OnSendClicked();
            _cancelButton.Accepting += (s, e) => OnCancelClicked();
            _commandField.KeyDown += OnCommandKeyDown;
            
            // Text change detection for autocomplete
            _commandField.KeyDown += (s, e) => 
            {
                if (e.Handled) return;
                    
                // Skip navigation keys when autocomplete is visible
                if (_autocomplete!.IsVisible)
                {
                    if (e == Key.CursorDown || e == Key.CursorUp || e == Key.Tab || e == Key.Esc || e == Key.Enter)
                        return;
                }
                
                Application.AddTimeout(TimeSpan.FromMilliseconds(50), () => 
                {
                    _autocomplete.ProcessTextChange(_commandField.Text ?? "");
                    return false;
                });
            };

            // Global ESC and Ctrl+C handler
            top.KeyDown += (s, e) =>
            {
                if (e == Key.Esc)
                {
                    if (_autocomplete!.IsVisible)
                    {
                        _autocomplete.Hide();
                        e.Handled = true;
                    }
                    else if (_isProcessing)
                    {
                        OnCancelClicked();
                        e.Handled = true;
                    }
                }
                else if (e == Key.C.WithCtrl)
                {
                    if (_isProcessing)
                    {
                        OnCancelClicked();
                        e.Handled = true;
                    }
                }
            };

            top.Add(_statusLabel);
            top.Add(_actionView);
            top.Add(_commandLabel);
            top.Add(_workLabel);
            top.Add(_commandField);
            top.Add(_sendButton);
            top.Add(_cancelButton);
            top.Add(_autocomplete.Frame);

            _commandField.SetFocus();
        }
        
        private void OnAutocompleteSelected(object? sender, ListViewItemEventArgs args)
        {
            if (args.Value is string selected)
            {
                var text = _commandField!.Text ?? "";
                var newText = _autocomplete!.ApplySelection(text, selected);
                
                _commandField.Text = newText;
                _commandField.MoveEnd();
                
                _autocomplete.Hide();
                _autocomplete.Reset();
                _commandField.SetFocus();
            }
        }

        private void OnCancelClicked()
        {
            if (_currentRequestCts != null && !_currentRequestCts.IsCancellationRequested)
            {
                _currentRequestCts.Cancel();
                AppendActionText("[Cancelling request...]", true);
            }
        }

        private void SetProcessingState(bool processing)
        {
            _isProcessing = processing;
            _sendButton!.Visible = !processing;
            _cancelButton!.Visible = processing;
            _commandField!.ReadOnly = processing;
            if (processing)
                _cancelButton.SetFocus();
            else
                _commandField.SetFocus();
        }

        private string GetStatusText()
        {
            var status = _isProcessing ? " | PROCESSING" : "";
            var cwd = Directory.GetCurrentDirectory();
            var shortCwd = TuiHelpers.ShortenPath(cwd, 30);
            return $"Dir: {shortCwd} | Model: {AgentConfig.Config.Model} | Stream: {(AgentConfig.Config.Stream ? "ON" : "OFF")}{status}";
        }

        private void UpdateStatus()
        {
            if (_statusLabel != null)
            {
                _statusLabel.Text = GetStatusText();
                _statusLabel.SetNeedsDraw();
            }
        }

        private void AppendActionText(string text, bool isError = false)
        {
            string styledText = isError ? $"[ERROR] {text}" : text;
            AppendToActionView(styledText + "\n");
        }
        
        /// <summary>
        /// Thread-safe append to action view with auto-scroll to end
        /// </summary>
        private void AppendToActionView(string text)
        {
            Application.AddTimeout(TimeSpan.Zero, () =>
            {
                try
                {
                    var currentText = _actionView!.Text ?? "";
                    _actionView.Text = currentText + text;
                    _actionView.MoveEnd();
                    _actionView.SetNeedsDraw();
                }
                catch { }
                return false;
            });
            Application.Wakeup();
        }

        private void AppendToolText(string toolName, string result)
        {
            AppendToolText(toolName, result, null);
        }
        
        private void AppendToolText(string toolName, string result, TimeSpan? elapsed)
        {
            var statusIcon = result.Contains("\"error\"") || result.Contains("\"timed_out\":true") ? "[X]" : "[OK]";
            var elapsedStr = elapsed.HasValue ? $" ({FormatElapsed(elapsed.Value)})" : "";
            AppendToActionView($"  TOOL {statusIcon} {toolName}{elapsedStr}\n");
        }
        
        private void UpdateToolProgress(ToolProgress progress)
        {
            // Capture values to avoid closure issues
            var toolName = progress.ToolName;
            var status = progress.Status;
            var elapsed = progress.ElapsedFormatted;
            
            // Use AddTimeout with 0ms delay for non-blocking UI update
            Application.AddTimeout(TimeSpan.Zero, () =>
            {
                try
                {
                    if (_workLabel != null)
                    {
                        var statusIcon = status switch
                        {
                            ToolStatus.Running => "⏳",
                            ToolStatus.Completed => "✓",
                            ToolStatus.Failed => "✗",
                            ToolStatus.TimedOut => "⏱",
                            ToolStatus.Cancelled => "⊘",
                            _ => "○"
                        };
                        _workLabel.Text = $"{statusIcon} {toolName} {elapsed}";
                        _workLabel.SetNeedsDraw();
                    }
                }
                catch { /* ignore UI errors */ }
                return false; // Don't repeat
            });
            Application.Wakeup();
        }
        
        private static string FormatElapsed(TimeSpan elapsed)
        {
            if (elapsed.TotalMinutes >= 1)
                return $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s";
            return $"{elapsed.TotalSeconds:F1}s";
        }
        
        /// <summary>
        /// Switch to orchestration mode with multi-panel layout
        /// </summary>
        private void EnterOrchestrationMode(int agentCount)
        {
            if (_orchestrationMode) return;
            _orchestrationMode = true;
            
            _orchestrationView!.Enter(agentCount, _actionView!, _commandLabel!, _workLabel!, _commandField!, _sendButton!, _cancelButton!);
        }
        
        /// <summary>
        /// Exit orchestration mode and return to normal layout
        /// </summary>
        private void ExitOrchestrationMode()
        {
            if (!_orchestrationMode) return;
            _orchestrationMode = false;
            
            _orchestrationView!.Exit(_actionView!, _commandLabel!, _workLabel!, _commandField!, _sendButton!, _cancelButton!);
        }
        
        /// <summary>
        /// Append text to agent-specific output view during orchestration
        /// </summary>
        private void AppendAgentOutput(string agentId, string text)
        {
            _orchestrationView?.AppendAgentOutput(agentId, text);
        }
        
        /// <summary>
        /// Append orchestrator status message
        /// </summary>
        private void AppendOrchestratorStatus(string text)
        {
            _orchestrationView?.AppendOrchestratorStatus(text, _actionView);
        }

        private void OnCommandKeyDown(object? sender, Key e)
        {
            // Ctrl+Enter to send
            if (e == Key.Enter.WithCtrl)
            {
                e.Handled = true;
                ProcessCommandAsync();
                return;
            }
            
            // Tab for autocomplete
            if (e == Key.Tab && _autocomplete!.IsVisible)
            {
                e.Handled = true;
                var selected = _autocomplete.GetSelectedItem();
                if (selected != null)
                {
                    OnAutocompleteSelected(null, new ListViewItemEventArgs(0, selected));
                }
                return;
            }
            
            // Arrow keys for autocomplete navigation
            if (_autocomplete!.IsVisible)
            {
                if (e == Key.CursorDown)
                {
                    e.Handled = true;
                    _autocomplete.MoveDown();
                    return;
                }
                if (e == Key.CursorUp)
                {
                    e.Handled = true;
                    _autocomplete.MoveUp();
                    return;
                }
            }
        }

        private void OnSendClicked()
        {
            ProcessCommandAsync();
        }

        private void ProcessCommandAsync()
        {
            var command = (_commandField!.Text ?? "").Trim().Replace("\r\n", " ").Replace("\n", " ");
            if (string.IsNullOrWhiteSpace(command))
                return;

            _autocomplete?.Hide();

            _commandField.Text = "";
            _commandField.SetNeedsDraw();

            AppendActionText($"USER> {command}");
            
            // Run everything on a background thread
            Task.Run(async () =>
            {
                try
                {
                    await HandleCommandAsync(command);
                }
                catch (Exception ex)
                {
                    Application.Invoke(() => AppendActionText($"Error: {ex.Message}", isError: true));
                }
                finally
                {
                    Application.Invoke(() => UpdateStatus());
                }
            });
        }

        private async Task HandleCommandAsync(string command)
        {
            // Simple commands - use Application.Invoke for UI updates
            if (command.Equals("/exit", StringComparison.OrdinalIgnoreCase))
            {
                PermissionManager.ClearSessionPermissions();
                Application.Invoke(() => Application.RequestStop());
                return;
            }

            if (command.Equals("/help", StringComparison.OrdinalIgnoreCase))
            {
                ShowHelp();
                return;
            }

            if (command.StartsWith("/clear", StringComparison.OrdinalIgnoreCase))
            {
                _messages = new List<ChatMessage> { new("system", _messages[0].Content) };
                AppendActionText("[OK] Conversation cleared.");
                return;
            }

            if (command.StartsWith("/stream", StringComparison.OrdinalIgnoreCase))
            {
                var arg = command.Length > 7 ? command[7..].Trim() : "";
                if (string.Equals(arg, "on", StringComparison.OrdinalIgnoreCase))
                    AgentConfig.Config.Stream = true;
                else if (string.Equals(arg, "off", StringComparison.OrdinalIgnoreCase))
                    AgentConfig.Config.Stream = false;
                else
                {
                    AppendActionText("Usage: /stream on|off", isError: true);
                    return;
                }
                AppendActionText($"[OK] Streaming: {(AgentConfig.Config.Stream ? "ON" : "OFF")}");
                Application.Invoke(() => UpdateStatus());
                return;
            }

            if (command.StartsWith("/models", StringComparison.OrdinalIgnoreCase))
            {
                HandleModelsCommand(command);
                return;
            }
            
            if (command.StartsWith("/plan", StringComparison.OrdinalIgnoreCase))
            {
                await HandlePlanCommandAsync(command);
                return;
            }
            
            if (command.StartsWith("/orchestrate", StringComparison.OrdinalIgnoreCase))
            {
                await HandleOrchestrateCommandAsync(command);
                return;
            }
            
            // Commands that delegate to CommandHandlers
            if (command.StartsWith("/config", StringComparison.OrdinalIgnoreCase))
            {
                await CommandHandlers.HandleConfigCommandAsync(command, _http, _appCancellationTokenSource.Token);
                return;
            }
            
            if (command.StartsWith("/set", StringComparison.OrdinalIgnoreCase))
            {
                await CommandHandlers.HandleSetCommandAsync(command, _http, _appCancellationTokenSource.Token);
                Application.Invoke(() => UpdateStatus());
                return;
            }
            
            if (command.StartsWith("/diff", StringComparison.OrdinalIgnoreCase))
            {
                await CommandHandlers.HandleDiffCommandAsync(command, _appCancellationTokenSource.Token, 
                    (msg, isError) => AppendActionText(msg, isError));
                return;
            }
            
            if (command.StartsWith("/test", StringComparison.OrdinalIgnoreCase))
            {
                AppendActionText("Running tests...");
                await CommandHandlers.HandleTestCommandAsync(command, _appCancellationTokenSource.Token,
                    (msg, isError) => AppendActionText(msg, isError));
                return;
            }
            
            if (command.StartsWith("/run ", StringComparison.OrdinalIgnoreCase))
            {
                await CommandHandlers.HandleRunCommandAsync(command, _appCancellationTokenSource.Token,
                    (msg, isError) => AppendActionText(msg, isError));
                return;
            }
            
            if (command.StartsWith("/commit", StringComparison.OrdinalIgnoreCase))
            {
                await CommandHandlers.HandleCommitCommandAsync(command, _appCancellationTokenSource.Token);
                AppendActionText("Commit completed.");
                return;
            }
            
            if (command.StartsWith("/push", StringComparison.OrdinalIgnoreCase))
            {
                await GitCommandHandlers.HandlePushCommandAsync(command, _appCancellationTokenSource.Token);
                AppendActionText("Push completed.");
                return;
            }
            
            if (command.StartsWith("/pull", StringComparison.OrdinalIgnoreCase))
            {
                await GitCommandHandlers.HandlePullCommandAsync(command, _appCancellationTokenSource.Token);
                AppendActionText("Pull completed.");
                return;
            }
            
            if (command.StartsWith("/rag", StringComparison.OrdinalIgnoreCase))
            {
                await RagCommandHandlers.HandleRagCommandAsync(command, _appCancellationTokenSource.Token);
                return;
            }
            
            if (command.StartsWith("/mcp", StringComparison.OrdinalIgnoreCase))
            {
                await McpCommandHandlers.HandleMcpCommandAsync(command, _appCancellationTokenSource.Token);
                return;
            }
            
            if (command.StartsWith("/health", StringComparison.OrdinalIgnoreCase))
            {
                AppendActionText("Running health check...");
                await HealthCheck.RunAllChecksAsync(_http);
                return;
            }
            
            if (command.StartsWith("/status", StringComparison.OrdinalIgnoreCase))
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"Messages: {_messages.Count}");
                sb.AppendLine($"Model: {AgentConfig.Config.Model}");
                sb.AppendLine($"Host: {AgentConfig.Config.HostUrl}");
                sb.AppendLine($"Stream: {AgentConfig.Config.Stream}");
                sb.AppendLine($"Work Dir: {Directory.GetCurrentDirectory()}");
                AppendActionText(sb.ToString());
                return;
            }
            
            if (command.StartsWith("/tokens", StringComparison.OrdinalIgnoreCase))
            {
                int totalTokens = 0;
                foreach (var msg in _messages)
                {
                    totalTokens += (msg.Content?.Length ?? 0) / 4; // rough estimate
                }
                AppendActionText($"Estimated tokens: ~{totalTokens} (based on {_messages.Count} messages)");
                return;
            }

            if (command.StartsWith("/summarize", StringComparison.OrdinalIgnoreCase))
            {
                AppendActionText("Summarizing conversation...");
                try
                {
                    var success = await AgentLoop.SummarizeConversationAsync(
                        _http, AgentConfig.Config.Model, _messages, CancellationToken.None,
                        s => Application.Invoke(() => AppendActionText($"  {s}")));
                    
                    Application.Invoke(() =>
                    {
                        if (success)
                            AppendActionText("✓ Conversation summarized successfully.");
                        else
                            AppendActionText("✗ Summarization failed or not enough messages.");
                    });
                }
                catch (Exception ex)
                {
                    Application.Invoke(() => AppendActionText($"✗ Summarization error: {ex.Message}"));
                }
                return;
            }

            // Regular chat - heavy work on background thread
            if (!string.IsNullOrWhiteSpace(command))
            {
                _messages.Add(new ChatMessage("user", command));

                _currentRequestCts?.Dispose();
                _currentRequestCts = new CancellationTokenSource();
                var ct = _currentRequestCts.Token;

                Application.Invoke(() => 
                {
                    SetProcessingState(true);
                    UpdateStatus();
                });

                try
                {
                    string? final;
                    if (AgentConfig.Config.Stream)
                    {
                        bool receivedTokens = false;
                        var tokenBuffer = new System.Text.StringBuilder();
                        int tokenCount = 0;
                        
                        _thinkingAnimationCts?.Cancel();
                        _thinkingAnimationCts = new CancellationTokenSource();
                        var thinkingToken = _thinkingAnimationCts.Token;
                        var startTime = DateTime.Now;
                        
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                while (!thinkingToken.IsCancellationRequested && !receivedTokens)
                                {
                                    var elapsed = (DateTime.Now - startTime).TotalSeconds;
                                    Application.Invoke(() =>
                                    {
                                        if (_workLabel != null)
                                        {
                                            _workLabel.Text = $"Waiting {elapsed:F0}s...";
                                            _workLabel.SetNeedsDraw();
                                        }
                                    });
                                    await Task.Delay(500, thinkingToken);
                                }
                            }
                            catch (OperationCanceledException) { }
                        }, thinkingToken);
                        
                        final = await AgentLoop.CompleteWithToolsStreamingAsync(
                            _http, AgentConfig.Config.Model, _messages, _tools, ct,
                            onToken: token => 
                            {
                                if (!receivedTokens)
                                {
                                    receivedTokens = true;
                                    _thinkingAnimationCts?.Cancel();
                                    AppendToActionView("ASSISTANT> ");
                                }
                                
                                tokenCount++;
                                tokenBuffer.Append(token);
                                
                                if (tokenBuffer.Length > 10 || token.Contains('\n'))
                                {
                                    var bufferedText = tokenBuffer.ToString();
                                    tokenBuffer.Clear();
                                    AppendToActionView(bufferedText);
                                }
                            },
                            onToolResult: (name, result) =>
                            {
                                _thinkingAnimationCts?.Cancel();
                                if (tokenBuffer.Length > 0)
                                {
                                    var bufferedText = tokenBuffer.ToString();
                                    tokenBuffer.Clear();
                                    AppendToActionView(bufferedText + "\n");
                                }
                            },
                            onUsage: usage =>
                            {
                                Application.AddTimeout(TimeSpan.Zero, () =>
                                {
                                    try
                                    {
                                        if (_workLabel != null)
                                        {
                                            _workLabel.Text = $"Tokens: {usage.TotalTokens}";
                                            _workLabel.SetNeedsDraw();
                                        }
                                    }
                                    catch { }
                                    return false;
                                });
                                Application.Wakeup();
                            },
                            onToolComplete: (name, result, elapsed) =>
                            {
                                AppendToolText(name, result, elapsed);
                            },
                            onToolProgress: UpdateToolProgress
                        );
                        
                        _thinkingAnimationCts?.Cancel();
                        
                        // Flush remaining buffer
                        if (tokenBuffer.Length > 0)
                        {
                            var bufferedText = tokenBuffer.ToString();
                            AppendToActionView(bufferedText);
                        }
                        
                        AppendToActionView("\n");
                    }
                    else
                    {
                        final = await AgentLoop.CompleteWithToolsAsync(
                            _http, AgentConfig.Config.Model, _messages, _tools, ct,
                            onToolResult: null,
                            onToolComplete: (name, result, elapsed) => AppendToolText(name, result, elapsed),
                            onToolProgress: UpdateToolProgress
                        );
                    }

                    if (!string.IsNullOrEmpty(final))
                    {
                        if (!AgentConfig.Config.Stream)
                            AppendActionText($"ASSISTANT> {final}");
                        _messages.Add(new ChatMessage("assistant", final));
                    }
                }
                catch (OperationCanceledException)
                {
                    _thinkingAnimationCts?.Cancel();
                    Application.AddTimeout(TimeSpan.Zero, () =>
                    {
                        try
                        {
                            if (_workLabel != null)
                            {
                                _workLabel.Text = " ";
                                _workLabel.SetNeedsDraw();
                            }
                        }
                        catch { }
                        return false;
                    });
                    Application.Wakeup();
                    AppendActionText("Request cancelled.", true);
                }
                catch (Exception ex)
                {
                    AppendActionText($"Error: {ex.Message}", true);
                }
                finally
                {
                    _thinkingAnimationCts?.Cancel();
                    _thinkingAnimationCts?.Dispose();
                    _thinkingAnimationCts = null;
                    
                    Application.AddTimeout(TimeSpan.Zero, () =>
                    {
                        try
                        {
                            if (_workLabel != null)
                            {
                                _workLabel.Text = " ";
                                _workLabel.SetNeedsDraw();
                            }
                            SetProcessingState(false);
                            UpdateStatus();
                        }
                        catch { }
                        return false;
                    });
                    Application.Wakeup();
                    
                    _currentRequestCts?.Dispose();
                    _currentRequestCts = null;
                }
            }
        }

        private void ShowHelp()
        {
            AppendActionText(@"
T.H.U.V.U. HELP
===============
Ctrl+Enter    Send message
/             Command autocomplete
@             File autocomplete (file: or dir: prefix)
Tab           Select autocomplete
Esc           Close autocomplete / Cancel

COMMANDS
--------
/help           Show this help
/exit           Quit
/clear          Reset conversation
/status         Show session status
/tokens         Estimate token usage

CONFIGURATION
-------------
/config         Show current configuration
/set KEY VALUE  Change setting
/stream on|off  Toggle streaming
/models list    List available models
/models use ID  Switch model

DEVELOPMENT
-----------
/diff           Show git diff
/test           Run dotnet tests
/run CMD        Run whitelisted command
/commit MSG     Commit with test gate
/push           Safe push with checks
/pull           Safe pull with autostash

ORCHESTRATION
-------------
/plan DESC      Create execution plan from task description
/orchestrate    Execute plan with multiple agents
  --agents N    Number of agents (1-8)
  --reset       Start fresh (reset all tasks)
  --retry       Retry failed tasks
  --skip        Skip failed dependencies (proceed anyway)
  --plan FILE   Use specific plan file

ADVANCED
--------
/rag            RAG operations (index, search, stats, clear)
/mcp            MCP code execution
/health         Run health checks

Permission prompts appear for write operations.
[A]lways | [S]ession | [O]nce | [N]o
");
        }

        private void HandleModelsCommand(string command)
        {
            var args = ConsoleHelpers.TokenizeArgs(command);
            var subCommand = args.Count > 1 ? args[1].ToLowerInvariant() : "list";

            switch (subCommand)
            {
                case "list":
                    AppendActionText("Models:");
                    foreach (var m in ModelRegistry.Instance.Models)
                    {
                        var isDefault = m.ModelId == ModelRegistry.Instance.DefaultModelId ? " *" : "";
                        AppendActionText($"  {(m.Enabled ? "[+]" : "[-]")} {m.DisplayName ?? m.ModelId}{isDefault}");
                    }
                    break;

                case "use":
                    if (args.Count < 3)
                    {
                        AppendActionText("Usage: /models use <model-id>", true);
                        return;
                    }
                    var model = ModelRegistry.Instance.GetModel(args[2]);
                    if (model == null)
                    {
                        AppendActionText($"Model '{args[2]}' not found", true);
                        return;
                    }
                    ModelRegistry.Instance.DefaultModelId = model.ModelId;
                    AgentConfig.Config.Model = model.ModelId;
                    AgentConfig.Config.HostUrl = model.HostUrl;
                    AgentConfig.Config.Stream = model.Stream;
                    AppendActionText($"[OK] Now using: {model.DisplayName ?? model.ModelId}");
                    UpdateStatus();
                    break;

                default:
                    AppendActionText("Usage: /models list | use <id>");
                    break;
            }
        }
        
        private async Task HandlePlanCommandAsync(string command)
        {
            var taskDescription = command.Length > 5 ? command[5..].Trim() : "";
            
            if (string.IsNullOrWhiteSpace(taskDescription))
            {
                AppendActionText(@"Usage: /plan <task description>

Examples:
  /plan Create a REST API for user management
  /plan Add unit tests for the Calculator class
  /plan Refactor the database layer

Analyzes a task and shows subtasks, estimated time, and recommended agent count.");
                return;
            }
            
            AppendActionText("Analyzing task and creating decomposition plan...");
            
            try
            {
                var decomposer = new TaskDecomposer(_http);
                var plan = await decomposer.DecomposeAsync(taskDescription, null, _appCancellationTokenSource.Token);
                
                // Save plan to files
                var jsonPath = TaskPlan.GetDefaultPlanPath();
                var mdPath = System.IO.Path.ChangeExtension(jsonPath, ".md");
                
                plan.SaveToFile(jsonPath);
                plan.SaveToMarkdown(mdPath);
                
                // Format plan for TUI display
                var sb = new System.Text.StringBuilder();
                sb.AppendLine();
                sb.AppendLine($"== Task Decomposition Plan ==");
                sb.AppendLine($"Task: {plan.OriginalRequest}");
                sb.AppendLine($"Summary: {plan.Summary}");
                sb.AppendLine();
                sb.AppendLine($"Recommended Agents: {plan.RecommendedAgentCount}  |  Est. Time: {plan.TotalEstimatedMinutes} min  |  Subtasks: {plan.SubTasks.Count}");
                sb.AppendLine();
                
                var groups = plan.GetParallelGroups();
                int groupNum = 1;
                
                foreach (var group in groups)
                {
                    var parallelLabel = group.Count > 1 ? $" (can run {group.Count} in parallel)" : "";
                    sb.AppendLine($"-- Phase {groupNum++}{parallelLabel} --");
                    
                    foreach (var task in group)
                    {
                        var icon = task.Type.ToString()[0];
                        sb.AppendLine($"  [{icon}] {task.Id}: {task.Title} (~{task.EstimatedMinutes}min)");
                        if (task.Dependencies.Any())
                        {
                            sb.AppendLine($"      depends on: {string.Join(", ", task.Dependencies)}");
                        }
                    }
                }
                
                sb.AppendLine();
                sb.AppendLine($"Risk: {plan.RiskAssessment}");
                sb.AppendLine();
                sb.AppendLine($"Strategy: {plan.ParallelizationStrategy}");
                sb.AppendLine();
                sb.AppendLine($"[Plan saved to: {jsonPath}]");
                sb.AppendLine($"Use '/orchestrate' to execute this plan.");
                
                AppendActionText(sb.ToString());
            }
            catch (Exception ex)
            {
                AppendActionText($"Failed to decompose task: {ex.Message}", isError: true);
            }
        }
        
        private async Task HandleOrchestrateCommandAsync(string command)
        {
            // Parse options
            var parts = ConsoleHelpers.TokenizeArgs(command);
            int? maxAgents = null;
            bool resetProgress = false;
            bool retryFailed = false;
            bool skipFailed = false;
            string? planFile = null;
            
            for (int i = 1; i < parts.Count; i++)
            {
                if (parts[i] == "--agents" && i + 1 < parts.Count && int.TryParse(parts[i + 1], out var n))
                {
                    maxAgents = Math.Clamp(n, 1, 8);
                    i++;
                }
                else if (parts[i] == "--reset")
                {
                    resetProgress = true;
                }
                else if (parts[i] == "--retry")
                {
                    retryFailed = true;
                }
                else if (parts[i] == "--skip")
                {
                    skipFailed = true;
                }
                else if (parts[i] == "--plan" && i + 1 < parts.Count)
                {
                    planFile = parts[++i];
                }
                else if (parts[i] == "--tui")
                {
                    // Already in TUI mode, ignore
                }
                else if (parts[i] == "help")
                {
                    AppendActionText(@"Usage: /orchestrate [options]

Options:
  --agents N     Number of agents (1-8, default: plan recommendation)
  --reset        Reset all tasks to pending (start fresh)
  --retry        Retry failed tasks (resets failed/blocked to pending)
  --skip         Skip failed dependencies (proceed with downstream tasks)
  --plan FILE    Use specific plan file (default: current-plan.json)

Completed tasks are automatically skipped (resume mode).
Use --retry to retry failed tasks, --skip to proceed despite failures,
or --reset to start completely over.");
                    return;
                }
            }
            
            // Load plan
            var currentDir = System.IO.Directory.GetCurrentDirectory();
            var planPath = planFile != null 
                ? (System.IO.Path.IsPathRooted(planFile) ? planFile : System.IO.Path.Combine(currentDir, planFile))
                : TaskPlan.GetDefaultPlanPath();
            
            if (!System.IO.File.Exists(planPath))
            {
                AppendActionText($"No plan found at {planPath}. Use '/plan <description>' first.", isError: true);
                return;
            }
            
            TaskPlan? plan;
            try
            {
                plan = TaskPlan.LoadFromFile(planPath);
                if (plan == null)
                {
                    AppendActionText("Failed to load plan file.", isError: true);
                    return;
                }
            }
            catch (Exception ex)
            {
                AppendActionText($"Error loading plan: {ex.Message}", isError: true);
                return;
            }
            
            // Handle reset
            if (resetProgress)
            {
                foreach (var task in plan.SubTasks)
                {
                    task.Status = SubTaskStatus.Pending;
                    task.AssignedAgentId = null;
                }
                plan.SaveToFile(planPath);
                AppendActionText("Reset all task statuses to Pending.");
            }
            // Handle retry (reset only failed/blocked/interrupted)
            else if (retryFailed)
            {
                var (pendingBefore, completedBefore, failedBefore, blockedBefore, inProgressBefore) = plan.GetStatusCounts();
                AppendActionText($"Before retry: Pending={pendingBefore}, InProgress={inProgressBefore}, Completed={completedBefore}, Failed={failedBefore}, Blocked={blockedBefore}");
                
                int resetCount = plan.ResetFailedTasks();
                plan.SaveToFile(planPath); // Always save after retry attempt
                
                if (resetCount > 0)
                {
                    AppendActionText($"Reset {resetCount} failed/blocked/interrupted task(s) to Pending.");
                }
                else
                {
                    AppendActionText("No tasks to retry (all tasks are either Pending or Completed).");
                }
            }
            
            // Check status
            var (pending, completed, failed, blocked, inProgress) = plan.GetStatusCounts();
            bool isResume = completed > 0 || failed > 0;
            
            // Check if orchestration can make progress
            if (!plan.CanMakeProgress())
            {
                AppendActionText($"Cannot make progress: no tasks are ready to run.\n" +
                    $"  Pending: {pending}, InProgress: {inProgress}, Completed: {completed}, Failed: {failed}, Blocked: {blocked}\n" +
                    $"  Use '--retry' to reset failed/blocked/interrupted tasks, or '--reset' to start over.",
                    isError: true);
                return;
            }
            
            var config = new OrchestratorConfig
            {
                MaxAgents = maxAgents ?? plan.RecommendedAgentCount,
                AutoMergeResults = true,
                UseProcessIsolation = false
            };
            
            var workDir = AgentConfig.GetWorkDirectory();
            
            // Enter orchestration mode with multi-panel UI
            EnterOrchestrationMode(config.MaxAgents);
            
            // Show status
            var sb = new System.Text.StringBuilder();
            if (isResume)
            {
                sb.AppendLine($"Resuming orchestration...");
                sb.AppendLine($"  Completed: {completed}, Failed: {failed}, Pending: {pending}, Blocked: {blocked}");
            }
            sb.AppendLine($"Starting orchestration with {config.MaxAgents} agent(s)...");
            sb.AppendLine($"  Plan: {plan.Summary}");
            sb.AppendLine($"  Remaining subtasks: {pending}");
            sb.AppendLine($"  Work directory: {workDir}");
            AppendOrchestratorStatus(sb.ToString());
            
            using var orchestrator = new TaskOrchestrator(_http, config, workDir);
            
            // Progress callbacks - these are called from background threads
            // so we just pass to the append methods which handle UI thread marshalling
            orchestrator.OnAgentStarted += (agentId, taskId) =>
            {
                AppendOrchestratorStatus($"  [{agentId}] Starting task {taskId}...");
                AppendAgentOutput(agentId, $"=== Starting task {taskId} ===\n");
            };
            
            orchestrator.OnTaskCompleted += (agentId, result) =>
            {
                var icon = result.Success ? "[OK]" : "[FAIL]";
                AppendOrchestratorStatus($"  [{agentId}] {icon} Task {result.TaskId} ({result.Duration.TotalSeconds:F1}s)");
                AppendAgentOutput(agentId, $"\n=== Task {result.TaskId} {(result.Success ? "completed" : "failed")} ({result.Duration.TotalSeconds:F1}s) ===\n");
                
                // Update plan file (in background, no UI involvement)
                var task = plan.SubTasks.FirstOrDefault(t => t.Id == result.TaskId);
                if (task != null)
                {
                    task.Status = result.Success ? SubTaskStatus.Completed : SubTaskStatus.Failed;
                    task.AssignedAgentId = agentId;
                    try { plan.SaveToFile(planPath); } catch { }
                }
            };
            
            orchestrator.OnPhaseCompleted += (phase) =>
            {
                AppendOrchestratorStatus($"  -- {phase} completed --");
            };
            
            // Streaming output from agents
            orchestrator.OnAgentOutput += (agentId, text) =>
            {
                AppendAgentOutput(agentId, text);
            };
            
            orchestrator.OnAgentToolCall += (agentId, toolName, status) =>
            {
                AppendAgentOutput(agentId, $"\n  [TOOL] {toolName}: {status}\n");
            };
            
            // Tool progress updates (use newline instead of carriage return)
            orchestrator.OnAgentToolProgress += (agentId, progress) =>
            {
                var elapsed = progress.Elapsed.TotalSeconds;
                var statusIcon = progress.Status switch
                {
                    ToolStatus.Running => "⏳",
                    ToolStatus.Completed => "✓",
                    ToolStatus.Failed => "✗",
                    ToolStatus.TimedOut => "⏱",
                    ToolStatus.Cancelled => "⊘",
                    _ => "•"
                };
                // Only show completed/failed/timeout status, skip in-progress updates to reduce noise
                if (progress.Status != ToolStatus.Running)
                {
                    AppendAgentOutput(agentId, $"  {statusIcon} {progress.ToolName} [{elapsed:F1}s]\n");
                }
            };
            
            // Start console redirection to prevent raw console output from corrupting TUI
            TuiConsoleRedirector.StartRedirection(text =>
            {
                // Redirect any Console.Write calls to the orchestrator status panel
                AppendOrchestratorStatus($"[Console] {text.TrimEnd()}");
            });
            
            try
            {
                var result = await orchestrator.ExecutePlanAsync(plan, _appCancellationTokenSource.Token, planPath, retryFailed, skipFailed);
                
                // Save final state
                plan.SaveToFile(planPath);
                plan.SaveToMarkdown(System.IO.Path.ChangeExtension(planPath, ".md"));
                
                // Show result
                var resultSb = new System.Text.StringBuilder();
                resultSb.AppendLine();
                resultSb.AppendLine(result.Success ? "=== Orchestration Completed ===" : "=== Orchestration Failed ===");
                resultSb.AppendLine($"Duration: {result.Duration.TotalMinutes:F1} minutes");
                resultSb.AppendLine($"Tasks: {result.CompletedCount} completed, {result.FailedCount} failed");
                
                if (!string.IsNullOrEmpty(result.Error))
                {
                    resultSb.AppendLine($"Error: {result.Error}");
                }
                
                AppendOrchestratorStatus(resultSb.ToString());
            }
            catch (OperationCanceledException)
            {
                plan.SaveToFile(planPath);
                AppendOrchestratorStatus("Orchestration cancelled. Progress saved.");
            }
            catch (Exception ex)
            {
                AppendOrchestratorStatus($"Orchestration failed: {ex.Message}");
            }
            finally
            {
                // Stop console redirection
                TuiConsoleRedirector.StopRedirection();
                
                // Exit orchestration mode
                ExitOrchestrationMode();
            }
        }
    }
}
