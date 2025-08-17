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
        private readonly CancellationTokenSource _cancellationTokenSource = new();

        // UI Components
        private Label? _statusLabel;
        private TextView? _actionView;
        private TextField? _commandField;
        private Button? _sendButton;

        public TuiInterface(HttpClient http, List<Tool> tools, List<ChatMessage> initialMessages)
        {
            _http = http;
            _tools = tools;
            _messages = initialMessages;
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
                _cancellationTokenSource.Cancel();
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
                    Normal = new Terminal.Gui.Attribute(Color.White, Color.Blue)
                }
            };

            // Action area (middle) - scrollable text view  
            _actionView = new TextView
            {
                X = 0,
                Y = 2,
                Width = Dim.Fill(),
                Height = Dim.Fill() - 4,
                ReadOnly = true,
                WordWrap = true,
                Text = "Welcome to T.H.U.V.U. Type commands or chat with the AI assistant.\nType /help for available commands.\n\n"
            };

            // Command area (bottom)
            var commandLabel = new Label("Command: ")
            {
                X = 0,
                Y = Pos.Bottom(_actionView)
            };

            _commandField = new TextField
            {
                X = Pos.Right(commandLabel),
                Y = Pos.Bottom(_actionView),
                Width = Dim.Fill() - 20,
                Height = 1
            };

            _sendButton = new Button("Send")
            {
                X = Pos.Right(_commandField) + 1,
                Y = Pos.Bottom(_actionView),
                IsDefault = true
            };

            // Event handlers
            _sendButton.Clicked += OnSendClicked;
            _commandField.KeyDown += OnCommandKeyDown;

            // Add components to Application.Top
            Application.Top.Add(_statusLabel);
            Application.Top.Add(_actionView);
            Application.Top.Add(commandLabel);
            Application.Top.Add(_commandField);
            Application.Top.Add(_sendButton);

            // Set focus to command field
            _commandField.SetFocus();
        }

        private string GetStatusText()
        {
            return $"Model: {AgentConfig.Config.Model} | Host: {AgentConfig.Config.HostUrl} | " +
                   $"Stream: {(AgentConfig.Config.StreamConfig ? "ON" : "OFF")} | " +
                   $"Messages: {_messages.Count}";
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
                
                // Add color coding for the text
                string coloredText;
                if (isError)
                {
                    coloredText = $"[ERROR] {text}";
                }
                else
                {
                    coloredText = text;
                }

                _actionView.Text = currentText + coloredText + "\n";
                
                // Scroll to bottom
                _actionView.MoveEnd();
                _actionView.SetNeedsDisplay();
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
                    AgentConfig.Config.StreamConfig = true;
                else if (string.Equals(arg, "off", StringComparison.OrdinalIgnoreCase))
                    AgentConfig.Config.StreamConfig = false;
                else
                {
                    AppendActionText("Usage: /stream on|off", isError: true);
                    return;
                }
                AppendSuccessText($"Streaming is now {(AgentConfig.Config.StreamConfig ? "ON" : "OFF")}.");
                return;
            }

            // For other commands that require the original Program methods,
            // we'll need to handle them here or delegate to the original implementation
            if (command.StartsWith("/diff", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    // Delegate to original implementation
                    await Program.HandleDiffCommandAsync(command, _cancellationTokenSource.Token, (text, isError) =>
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

            if (command.StartsWith("/test", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await Program.HandleTestCommandAsync(command, _cancellationTokenSource.Token, (text, isError) =>
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
                    await Program.HandleRunCommandAsync(command, _cancellationTokenSource.Token, (text, isError) =>
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

                AppendActionText("Processing...");

                try
                {
                    string? final;
                    if (AgentConfig.Config.StreamConfig)
                    {
                        final = await Program.CompleteWithToolsStreamingAsync(
                            _http, AgentConfig.Config.Model, _messages, _tools, _cancellationTokenSource.Token,
                            onToken: token => Application.MainLoop.Invoke(() =>
                            {
                                var currentText = _actionView!.Text.ToString();
                                _actionView.Text = currentText + token;
                                _actionView.MoveEnd();
                                _actionView.SetNeedsDisplay();
                            }),
                            onToolResult: (name, result) =>
                            {
                                AppendToolText($"{name} => {result}");
                            },
                            onUsage: usage =>
                            {
                                AppendTokenText($"prompt={usage.PromptTokens}, completion={usage.CompletionTokens}, total={usage.TotalTokens}");
                            }
                        );
                    }
                    else
                    {
                        final = await Program.CompleteWithToolsAsync(
                            _http, AgentConfig.Config.Model, _messages, _tools, _cancellationTokenSource.Token,
                            onToolResult: (name, result) =>
                            {
                                AppendToolText($"{name} => {result}");
                            }
                        );
                    }

                    if (!string.IsNullOrEmpty(final))
                    {
                        AppendActionText($"\n{final}\n");
                        _messages.Add(new ChatMessage("assistant", final));
                    }
                    else
                    {
                        AppendActionText("(no content)");
                    }
                }
                catch (Exception ex)
                {
                    AppendActionText($"Error processing message: {ex.Message}", true);
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