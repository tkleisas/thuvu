using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Terminal.Gui;
using thuvu.Models;
using CodingAgent;
using thuvu.Tools;

namespace thuvu
{
    /// <summary>
    /// Terminal.GUI based interface for THUVU that divides the screen into 3 areas:
    /// - Status area (top): shows model and parameters
    /// - Action area (middle): shows requests, tool calls and responses
    /// - Command area (bottom): user input
    /// </summary>
    public class TuiInterface
    {
        private readonly HttpClient _http;
        private readonly List<Tool> _tools;
        private List<ChatMessage> _messages;
        private readonly CancellationTokenSource _appCancellationTokenSource = new();
        private CancellationTokenSource? _currentRequestCts;
        private bool _isProcessing = false;

        // UI Components
        private Label? _statusLabel;
        private Label? _workLabel;
        private Label? _commandLabel;
        private TextView? _actionView;
        private TextField? _commandField;
        private Button? _sendButton;
        private Button? _cancelButton;
        private string workanim = "-\\|/";
        private int workanimIdx = 0;
        public TuiInterface(HttpClient http, List<Tool> tools, List<ChatMessage> initialMessages)
        {
            _http = http;
            _tools = tools;
            _messages = initialMessages;
        }
        public void Animate()
        {
            if(workanimIdx< workanim.Length-1)

                workanimIdx++;
            else
                workanimIdx = 0;
            _workLabel.Text = workanim.Substring(workanimIdx,1);
        }
        public void Run()
        {
            Application.Init();
            
            try
            {
                SetupUi();
                Application.Run();
            }
            finally
            {
                Application.Shutdown();
                _appCancellationTokenSource.Cancel();
            }
        }

        private void SetupUi()
        {
            // Status area (top)
            _statusLabel = new Label
            {
                X = 0,
                Y = 0,
                Height = 1,
                Width = Dim.Fill(),
                Text = GetStatusText(),
                ColorScheme = new ColorScheme
                {
                    Normal = new Terminal.Gui.Attribute(Color.Green, Color.Black)
                }
            };
            string banner = "                                         \n"+
                            "███████ █     █ █      █ █     █ █      █\n"+
                            "   █    █     █ █      █ █     █ █      █\n"+
                            "   █    ███████ █      █  █   █  █      █\n"+
                            "   █    █     █ █      █   █ █   █      █\n"+
                            "   █    █     █  ██████     █     ██████ \n"+
                            "                                         \n";
            // Action area (middle) - scrollable text view  
            _actionView = new TextView
            {
                X = 0,
                Y = 2,
                Width = Dim.Fill(),
                Height = Dim.Fill() - 4,
                ReadOnly = true,
                WordWrap = true,
                ColorScheme = new ColorScheme
                {
                    Normal = new Terminal.Gui.Attribute(Color.White, Color.Black),
                    Focus = new Terminal.Gui.Attribute(Color.BrightYellow, Color.Black)
                },
                Text = banner+"Welcome to T.H.U.V.U. Type commands or chat with the AI assistant.\nType /help for available commands.\n\n"
            };

            // Command area (bottom)
            _commandLabel = new Label("Command: ")
            {
                X = 0,
                Y = Pos.Bottom(_actionView)
            };
            _workLabel = new Label(" ")
            {
                X = Pos.Right(_commandLabel),
                Y = Pos.Bottom(_actionView),
                Width = 1,
                Height = 1,
                ColorScheme = new ColorScheme
                {
                    Normal = new Terminal.Gui.Attribute(Color.Gray, Color.Black)
                }
            };
            _commandField = new TextField
            {
                X = Pos.Right(_workLabel),
                Y = Pos.Bottom(_actionView),
                Width = Dim.Fill() - 20,
                Height = 1,
                ColorScheme = new ColorScheme
                {
                    Normal = new Terminal.Gui.Attribute(Color.BrightYellow,Color.Black),
                    Focus = new Terminal.Gui.Attribute(Color.BrightYellow,Color.Red)
                }
            };

            _sendButton = new Button("Send")
            {
                X = Pos.Right(_commandField) + 1,
                Y = Pos.Bottom(_actionView),
                IsDefault = false
            };

            _cancelButton = new Button("Cancel")
            {
                X = Pos.Right(_sendButton) + 1,
                Y = Pos.Bottom(_actionView),
                Visible = false // Hidden by default, shown during processing
            };

            // Event handlers
            _sendButton.Clicked += OnSendClicked;
            _cancelButton.Clicked += OnCancelClicked;
            _commandField.KeyDown += OnCommandKeyDown;

            // Global key handler for ESC to cancel
            Application.Top.KeyDown += (e) =>
            {
                if (e.KeyEvent.Key == Key.Esc && _isProcessing)
                {
                    OnCancelClicked();
                    e.Handled = true;
                }
            };

            // Add components to Application.Top
            Application.Top.Add(_statusLabel);
            Application.Top.Add(_actionView);
            Application.Top.Add(_commandLabel);
            Application.Top.Add(_workLabel);
            Application.Top.Add(_commandField);
            Application.Top.Add(_sendButton);
            Application.Top.Add(_cancelButton);

            // Set focus to command field
            _commandField.SetFocus();
        }

        private void OnCancelClicked()
        {
            if (_currentRequestCts != null && !_currentRequestCts.IsCancellationRequested)
            {
                _currentRequestCts.Cancel();
                AppendActionText($"[{DateTime.Now:HH:mm:ss}] Cancelling request...", true);
            }
        }

        private void SetProcessingState(bool processing)
        {
            _isProcessing = processing;
            Application.MainLoop.Invoke(() =>
            {
                _sendButton!.Visible = !processing;
                _cancelButton!.Visible = processing;
                _commandField!.ReadOnly = processing;
                if (processing)
                {
                    _cancelButton.SetFocus();
                }
                else
                {
                    _commandField.SetFocus();
                }
                Application.Refresh();
            });
        }

        private string GetStatusText()
        {
            var status = _isProcessing ? " | Status: PROCESSING (ESC to cancel)" : "";
            return $"Model: {AgentConfig.Config.Model} | Host: {AgentConfig.Config.HostUrl} | " +
                   $"Stream: {(AgentConfig.Config.Stream ? "ON" : "OFF")} | " +
                   $"Messages: {_messages.Count}{status}";
        }

        private void UpdateStatus()
        {
            _statusLabel.Text = GetStatusText();
            Application.MainLoop.Invoke(() => _statusLabel.SetNeedsDisplay());
        }

        private void AppendActionText(string text, bool isError = false)
        {
            Application.MainLoop.Invoke(() =>
            {
                var currentText = _actionView!.Text.ToString();
                
                // Add color coding and timestamp for the text
                string coloredText;
                if (isError)
                {
                    coloredText = $"[ERROR {DateTime.Now:HH:mm:ss}] {text}";
                }
                else
                {
                    coloredText = text;
                }

                _actionView.Text = currentText + coloredText + "\n";
                
                // Scroll to bottom
                _actionView.MoveEnd();
                _actionView.SetNeedsDisplay();
                Application.Refresh();
            });
        }

        private void AppendSuccessText(string text)
        {
            Application.MainLoop.Invoke(() =>
            {
                var currentText = _actionView!.Text.ToString();
                _actionView.Text = currentText + $"[SUCCESS] {text}\n";
                
                // Scroll to bottom
                _actionView.MoveEnd();
                _actionView.SetNeedsDisplay();
            });
        }

        private void AppendToolText(string text)
        {
            Application.MainLoop.Invoke(() =>
            {
                var currentText = _actionView!.Text.ToString();
                _actionView.Text = currentText + $"[TOOL] {text}\n";
                
                // Scroll to bottom
                _actionView.MoveEnd();
                _actionView.SetNeedsDisplay();
            });
        }

        private void AppendTokenText(string text)
        {
            Application.MainLoop.Invoke(() =>
            {
                var currentText = _actionView!.Text.ToString();
                _actionView.Text = currentText + $"[TOKENS] {text}\n";
                
                // Scroll to bottom
                _actionView.MoveEnd();
                _actionView.SetNeedsDisplay();
            });
        }

        private void OnCommandKeyDown(View.KeyEventEventArgs e)
        {
            if (e.KeyEvent.Key == Key.Enter)
            {
                e.Handled = true;
                _ = ProcessCommandAsync();
            }
        }

        private void OnSendClicked()
        {
            _ = ProcessCommandAsync();
        }

        private async Task ProcessCommandAsync()
        {
            var command = _commandField.Text.ToString().Trim();
            if (string.IsNullOrWhiteSpace(command))
                return;

            // Clear the command field
            Application.MainLoop.Invoke(() =>
            {
                _commandField.Text = "";
                _commandField.SetNeedsDisplay();
            });

            // Display the user command
            AppendActionText($"> {command}");

            try
            {
                await HandleCommandAsync(command);
            }
            catch (Exception ex)
            {
                AppendActionText($"Error processing command: {ex.Message}", isError: true);
            }

            UpdateStatus();
        }

        private async Task HandleCommandAsync(string command)
        {
            // Handle special commands
            if (command.Equals("/exit", StringComparison.OrdinalIgnoreCase))
            {
                Models.PermissionManager.ClearSessionPermissions();
                Application.RequestStop();
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
                AppendSuccessText("Conversation cleared.");
                return;
            }

            if (command.StartsWith("/system ", StringComparison.OrdinalIgnoreCase))
            {
                var sys = command.Substring(8).Trim();
                if (string.IsNullOrWhiteSpace(sys))
                {
                    AppendActionText("Usage: /system <text>", isError: true);
                    return;
                }
                _messages[0] = new ChatMessage("system", sys);
                AppendSuccessText("System prompt updated.");
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
                AppendSuccessText($"Streaming is now {(AgentConfig.Config.Stream ? "ON" : "OFF")}.");
                UpdateStatus();
                return;
            }

            // For other commands that require the original Program methods,
            // we'll need to handle them here or delegate to the original implementation
            if (command.StartsWith("/diff", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    // Delegate to original implementation
                    await CommandHandlers.HandleDiffCommandAsync(command, _appCancellationTokenSource.Token, (text, isError) =>
                    {
                        if (isError)
                            AppendActionText(text, true);
                        else
                            AppendActionText(text);
                    });
                }
                catch (Exception ex)
                {
                    AppendActionText($"Error executing diff command: {ex.Message}", true);
                }
                return;
            }
            if(command.StartsWith("/set", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var args = ConsoleHelpers.TokenizeArgs(command); // you already have this helper
                    if (args.Count < 3)
                    {
                        AppendSuccessText("Usage: /set model <id> | /set host <url> | /set stream on|off | /set timeout <ms> | /set httptimeout <minutes>");
                        return;
                    }

                    var key = args[1].ToLowerInvariant();
                    switch (key)
                    {
                        case "model":
                            {
                                var id = string.Join(' ', args.Skip(2));
                                if (string.IsNullOrWhiteSpace(id)) { AppendSuccessText("Model id required."); break; }
                                AgentConfig.Config.Model = id.Trim();
                                AgentConfig.SaveConfig();
                                AppendSuccessText($"Model set to: {AgentConfig.Config.Model}");
                                UpdateStatus();
                                return;
                            }
                        case "host":
                            {
                                var url = string.Join(' ', args.Skip(2));
                                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                                {
                                    AppendActionText("Please provide a valid http(s) URL, e.g. http://127.0.0.1:1234");
                                    return;
                                }
                                AgentConfig.Config.HostUrl = uri.ToString().TrimEnd('/'); // normalize (optional)
                                AgentConfig.SaveConfig();
                                AppendActionText($"Host set to: {AgentConfig.Config.HostUrl}");
                                AppendActionText("Note: Restart the application for the host change to take effect.");
                                UpdateStatus();
                                return;
                            }
                        case "stream":
                            {
                                var v = args[2].ToLowerInvariant();
                                if (v is "on" or "off")
                                {
                                    AgentConfig.Config.Stream = v == "on";

                                    AgentConfig.SaveConfig();
                                    AppendActionText($"Streaming is now {(AgentConfig.Config.Stream ? "ON" : "OFF")}.");
                                }
                                else AppendActionText("Usage: /set stream on|off");
                                UpdateStatus();
                                return;
                            }
                        case "timeout":
                            {
                                if (!int.TryParse(args[2], out var ms) || ms < 1000 || ms > 600_000)
                                {
                                    AppendActionText("Timeout must be 1000..600000 ms.");
                                    return;
                                }
                                AgentConfig.Config.TimeoutMs = ms;
                                AgentConfig.SaveConfig();
                                AppendActionText($"Default process timeout set to {AgentConfig.Config.TimeoutMs} ms.");
                                UpdateStatus();
                                return;
                            }
                        case "httptimeout":
                            {
                                if (!int.TryParse(args[2], out var minutes) || minutes < 1 || minutes > 120)
                                {
                                    AppendActionText("HTTP timeout must be 1..120 minutes.");
                                    return;
                                }
                                AgentConfig.Config.HttpRequestTimeout = minutes;
                                AgentConfig.SaveConfig();
                                AppendActionText($"HTTP request timeout set to {AgentConfig.Config.HttpRequestTimeout} minutes.");
                                AppendActionText("Note: Restart the application for the timeout change to take effect.");
                                UpdateStatus();
                                return;
                            }
                        default:
                            AppendActionText("Supported keys: model, host, stream, timeout, httptimeout");
                            break;

                    }
                }
                catch (Exception ex)
                {
                    AppendActionText($"Error executing set command: {ex.Message}", true);
                }
            }
            if (command.StartsWith("/test", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await CommandHandlers.HandleTestCommandAsync(command, _appCancellationTokenSource.Token, (text, isError) =>
                    {
                        if (isError)
                            AppendActionText(text, true);
                        else
                            AppendActionText(text);
                    });
                }
                catch (Exception ex)
                {
                    AppendActionText($"Error executing test command: {ex.Message}", true);
                }
                return;
            }

            if (command.StartsWith("/run", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await CommandHandlers.HandleRunCommandAsync(command, _appCancellationTokenSource.Token, (text, isError) =>
                    {
                        if (isError)
                            AppendActionText(text, true);
                        else
                            AppendActionText(text);
                    });
                }
                catch (Exception ex)
                {
                    AppendActionText($"Error executing run command: {ex.Message}", true);
                }
                return;
            }

            // Handle regular chat message
            if (!string.IsNullOrWhiteSpace(command))
            {
                _messages.Add(new ChatMessage("user", command));

                // Create cancellation token for this request
                _currentRequestCts?.Dispose();
                _currentRequestCts = new CancellationTokenSource();
                var ct = _currentRequestCts.Token;

                SetProcessingState(true);
                UpdateStatus();

                AppendActionText($"[{DateTime.Now:HH:mm:ss}] Sending to {AgentConfig.Config.Model}... (Press ESC to cancel)");
                bool finished = false;
                int iterationCount = 0;
                try
                {
                    while (!finished && !ct.IsCancellationRequested)
                    {
                        iterationCount++;
                        AppendActionText($"[{DateTime.Now:HH:mm:ss}] Agent iteration {iterationCount} - waiting for LLM response...");
                        
                        string? final;
                        if (AgentConfig.Config.Stream)
                        {
                            // Add a flag to track if we've received any tokens
                            bool receivedTokens = false;
                            var tokenBuffer = new System.Text.StringBuilder();
                            
                            final = await AgentLoop.CompleteWithToolsStreamingAsync(
                                _http, AgentConfig.Config.Model, _messages, _tools, ct,
                                onToken: token => 
                                {
                                    if (!receivedTokens)
                                    {
                                        receivedTokens = true;
                                        Application.MainLoop.Invoke(() =>
                                        {
                                            var currentText = _actionView!.Text.ToString();
                                            _actionView.Text = currentText + $"[{DateTime.Now:HH:mm:ss}] Streaming response:\n";
                                            _actionView.MoveEnd();
                                            _actionView.SetNeedsDisplay();
                                            Application.Refresh();
                                        });
                                    }
                                    
                                    // Buffer tokens and flush periodically for better performance
                                    tokenBuffer.Append(token);
                                    if (tokenBuffer.Length > 10 || token.Contains('\n'))
                                    {
                                        var bufferedText = tokenBuffer.ToString();
                                        tokenBuffer.Clear();
                                        Application.MainLoop.Invoke(() =>
                                        {
                                            var currentText = _actionView!.Text.ToString();
                                            _actionView.Text = currentText + bufferedText;
                                            _actionView.MoveEnd();
                                            _actionView.SetNeedsDisplay();
                                            Application.Refresh();
                                            Animate();
                                        });
                                    }
                                },
                                onToolResult: (name, result) =>
                                {
                                    // Flush any remaining tokens before showing tool result
                                    if (tokenBuffer.Length > 0)
                                    {
                                        var bufferedText = tokenBuffer.ToString();
                                        tokenBuffer.Clear();
                                        Application.MainLoop.Invoke(() =>
                                        {
                                            var currentText = _actionView!.Text.ToString();
                                            _actionView.Text = currentText + bufferedText + "\n";
                                            _actionView.SetNeedsDisplay();
                                            Application.Refresh();
                                        });
                                    }
                                    AppendToolText($"{name} => {(result.Length > 200 ? result.Substring(0, 200) + "..." : result)}");
                                    Animate();
                                },
                                onUsage: usage =>
                                {
                                    AppendTokenText($"prompt={usage.PromptTokens}, completion={usage.CompletionTokens}, total={usage.TotalTokens}");
                                    Animate();
                                }
                            );
                            
                            // Flush any remaining buffered tokens
                            if (tokenBuffer.Length > 0)
                            {
                                var bufferedText = tokenBuffer.ToString();
                                Application.MainLoop.Invoke(() =>
                                {
                                    var currentText = _actionView!.Text.ToString();
                                    _actionView.Text = currentText + bufferedText;
                                    _actionView.MoveEnd();
                                    _actionView.SetNeedsDisplay();
                                    Application.Refresh();
                                });
                            }
                        }
                        else
                        {
                            final = await AgentLoop.CompleteWithToolsAsync(
                                _http, AgentConfig.Config.Model, _messages, _tools, ct,
                                onToolResult: (name, result) =>
                                {
                                    AppendToolText($"{name} => {(result.Length > 200 ? result.Substring(0, 200) + "..." : result)}");
                                    Animate();
                                }
                            );
                        }

                        if (!string.IsNullOrEmpty(final))
                        {
                            if (!AgentConfig.Config.Stream)
                            {
                                // Only append final text if not streaming (streaming already shows it)
                                AppendActionText($"\n{final}\n");
                            }
                            else
                            {
                                AppendActionText(""); // Just add a newline after streaming
                            }
                            _messages.Add(new ChatMessage("assistant", final));
                            Animate();
                            if(final.IndexOf("Finished Tasks.")>=0)
                            {
                                AppendSuccessText("Agent completed all tasks.");
                                finished = true;
                            }
                        }
                        else
                        {
                            AppendActionText($"[{DateTime.Now:HH:mm:ss}] (no content in response)");
                        }
                    }
                }
                catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
                {
                    AppendActionText($"[{DateTime.Now:HH:mm:ss}] Request timed out. Try increasing /set httptimeout", true);
                }
                catch (OperationCanceledException)
                {
                    AppendActionText($"[{DateTime.Now:HH:mm:ss}] Request cancelled by user.", true);
                    // Remove the last user message since we cancelled the request
                    if (_messages.Count > 1 && _messages[^1].Role == "user")
                    {
                        _messages.RemoveAt(_messages.Count - 1);
                    }
                }
                catch (HttpRequestException ex)
                {
                    AppendActionText($"[{DateTime.Now:HH:mm:ss}] HTTP Error: {ex.Message} - Is the LLM server running at {AgentConfig.Config.HostUrl}?", true);
                }
                catch (Exception ex)
                {
                    AppendActionText($"[{DateTime.Now:HH:mm:ss}] Error: {ex.GetType().Name}: {ex.Message}", true);
                }
                finally
                {
                    // Reset processing state
                    SetProcessingState(false);
                    UpdateStatus();
                    _currentRequestCts?.Dispose();
                    _currentRequestCts = null;
                }
            }
        }

        private void ShowHelp()
        {
            var helpText = @"(T)ool for (H)eurustic (U)niversal (V)ersatile (U)sage (THUVU)
Commands:
  /help                         Show this help
  /exit                         Quit
  /clear                        Reset conversation (keeps current system prompt)
  /system <text>                Set system prompt
  /stream on|off                Toggle token-by-token streaming
  /diff [--staged] [--context N] [--root PATH] [PATH ...]
       Show a git unified diff. Example: /diff --staged --context 5 src/
  /test [SOLUTION_OR_PROJECT] [--filter EXP] [--logger trx|console]
       Run dotnet tests and print a summary
  /run CMD [ARGS ...] [--cwd PATH] [--timeout MS]
       Run a whitelisted command (dotnet, git, bash, powershell)

Permission System:
  Read-only tools are always allowed.
  Write tools require user permission with options:
    [A] Always for this repo (persistent)
    [S] For this session (temporary)  
    [O] Once (this time only)
    [N] No (cancel operation)";

            AppendActionText(helpText);
        }
    }
}
