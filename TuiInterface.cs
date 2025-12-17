using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Terminal.Gui;
using thuvu.Models;
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
        private ListView? _autocompleteList;
        private FrameView? _autocompleteFrame;
        private ObservableCollection<string> _autocompleteItems = new();
        private string _autocompletePrefix = "";
        private int _autocompleteStartPos = 0;
        
        public TuiInterface(HttpClient http, List<Tool> tools, List<ChatMessage> initialMessages)
        {
            _http = http;
            _tools = tools;
            _messages = initialMessages;
        }
        
        public void Run()
        {
            Application.Init();
            
            // Set up TUI permission prompt handler
            PermissionManager.CustomPermissionPrompt = TuiPermissionPrompt;
            
            // Set up Ctrl+C handler as fallback (in case Terminal.Gui doesn't intercept it)
            Console.CancelKeyPress += OnConsoleCancelKeyPress;
            
            try
            {
                var top = new Toplevel();
                SetupUi(top);
                Application.Run(top);
            }
            finally
            {
                Console.CancelKeyPress -= OnConsoleCancelKeyPress;
                PermissionManager.CustomPermissionPrompt = null;
                Application.Shutdown();
                _appCancellationTokenSource.Cancel();
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
        
        /// <summary>
        /// TUI-compatible permission prompt using Dialog
        /// </summary>
        private char TuiPermissionPrompt(string toolName, string argsJson)
        {
            char result = 'N'; // Default to deny
            
            // Use Invoke to run on UI thread and wait for completion
            var completionEvent = new ManualResetEventSlim(false);
            
            Application.Invoke(() =>
            {
                try
                {
                    // Create buttons
                    var alwaysBtn = new Button { Text = "_Always" };
                    var sessionBtn = new Button { Text = "_Session" };
                    var onceBtn = new Button { Text = "_Once" };
                    var noBtn = new Button { Text = "_No" };
                    
                    // Create dialog with buttons
                    var dialog = new Dialog
                    {
                        Title = "Permission Required",
                        Width = 65,
                        Height = 14,
                        Buttons = [alwaysBtn, sessionBtn, onceBtn, noBtn]
                    };
                    
                    // Add content labels
                    var toolLabel = new Label
                    {
                        X = 1,
                        Y = 1,
                        Text = $"Tool: {toolName}"
                    };
                    
                    var argsDisplay = argsJson.Length > 50 ? argsJson.Substring(0, 47) + "..." : argsJson;
                    var argsLabel = new Label
                    {
                        X = 1,
                        Y = 3,
                        Width = Dim.Fill() - 2,
                        Text = $"Args: {argsDisplay}"
                    };
                    
                    var questionLabel = new Label
                    {
                        X = 1,
                        Y = 5,
                        Text = "Allow this operation?"
                    };
                    
                    var hintLabel = new Label
                    {
                        X = 1,
                        Y = 7,
                        ColorScheme = new ColorScheme { Normal = new TgAttribute(Color.DarkGray, Color.Black) },
                        Text = "[A]lways=persist | [S]ession=temp | [O]nce | [N]o=deny"
                    };
                    
                    dialog.Add(toolLabel, argsLabel, questionLabel, hintLabel);
                    
                    // Button handlers
                    alwaysBtn.Accepting += (s, e) => { result = 'A'; Application.RequestStop(); };
                    sessionBtn.Accepting += (s, e) => { result = 'S'; Application.RequestStop(); };
                    onceBtn.Accepting += (s, e) => { result = 'O'; Application.RequestStop(); };
                    noBtn.Accepting += (s, e) => { result = 'N'; Application.RequestStop(); };
                    
                    // Run the dialog modally
                    Application.Run(dialog);
                    dialog.Dispose();
                }
                finally
                {
                    completionEvent.Set();
                }
            });
            
            // Wait for dialog to complete
            completionEvent.Wait();
            
            // Log the result
            var action = result switch
            {
                'A' => "Always allowed",
                'S' => "Session allowed", 
                'O' => "Once allowed",
                _ => "Denied"
            };
            SessionLogger.Instance.LogInfo($"Permission {action} for tool: {toolName}");
            
            // Update action view
            Application.Invoke(() =>
            {
                if (_actionView != null)
                {
                    var currentText = _actionView.Text ?? "";
                    var timestamp = DateTime.Now.ToString("HH:mm:ss");
                    var icon = result == 'N' ? "[DENY]" : "[PERMIT]";
                    _actionView.Text = currentText + $"  [{timestamp}] {icon} {toolName}\n";
                    _actionView.MoveEnd();
                    _actionView.SetNeedsDraw();
                }
            });
            
            return result;
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
                ColorScheme = new ColorScheme
                {
                    Normal = new TgAttribute(Color.Green, Color.Black)
                }
            };
            
            string banner = 
                "╔══════════════════════════════════════════════════════════════╗\n"+
                "║  T.H.U.V.U. - Tool for Heuristic Universal Versatile Usage   ║\n"+
                "╚══════════════════════════════════════════════════════════════╝\n";
            
            // Action area (middle) - scrollable text view  
            _actionView = new TextView
            {
                X = 0,
                Y = 2,
                Width = Dim.Fill(),
                Height = Dim.Fill() - 7,
                ReadOnly = true,
                WordWrap = true,
                ColorScheme = new ColorScheme
                {
                    Normal = new TgAttribute(Color.White, Color.Black),
                    Focus = new TgAttribute(Color.BrightYellow, Color.Black)
                },
                Text = banner + "Welcome! Type commands or chat. Ctrl+Enter to send. /help for commands.\n\n"
            };

            // Command area labels
            _commandLabel = new Label
            {
                X = 0,
                Y = Pos.Bottom(_actionView),
                Text = "Command (Ctrl+Enter): ",
                ColorScheme = new ColorScheme
                {
                    Normal = new TgAttribute(Color.DarkGray, Color.Black)
                }
            };
            
            _workLabel = new Label
            {
                X = Pos.Right(_commandLabel),
                Y = Pos.Bottom(_actionView),
                Width = 30,
                Height = 1,
                Text = " ",
                ColorScheme = new ColorScheme
                {
                    Normal = new TgAttribute(Color.Cyan, Color.Black)
                }
            };
            
            // Multi-line command input
            _commandField = new TextView
            {
                X = 0,
                Y = Pos.Bottom(_actionView) + 1,
                Width = Dim.Fill() - 12,
                Height = 4,
                WordWrap = true,
                ColorScheme = new ColorScheme
                {
                    Normal = new TgAttribute(Color.BrightYellow, Color.Black),
                    Focus = new TgAttribute(Color.BrightYellow, Color.DarkGray)
                }
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
            
            // Autocomplete popup
            _autocompleteFrame = new FrameView
            {
                X = 2,
                Y = 10,
                Width = 60,
                Height = 12,
                Visible = false,
                Title = "Files (Tab=select, Esc=close)",
                ColorScheme = new ColorScheme
                {
                    Normal = new TgAttribute(Color.Black, Color.Gray),
                    Focus = new TgAttribute(Color.Black, Color.Gray)
                }
            };
            
            _autocompleteList = new ListView
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill(),
                CanFocus = true,
                Source = new ListWrapper<string>(_autocompleteItems),
                ColorScheme = new ColorScheme
                {
                    Normal = new TgAttribute(Color.Black, Color.Gray),
                    Focus = new TgAttribute(Color.White, Color.Blue)
                }
            };
            _autocompleteFrame.Add(_autocompleteList);
            
            _autocompleteList.OpenSelectedItem += OnAutocompleteSelected;

            // Event handlers
            _sendButton.Accepting += (s, e) => OnSendClicked();
            _cancelButton.Accepting += (s, e) => OnCancelClicked();
            _commandField.KeyDown += OnCommandKeyDown;
            
            // Text change detection for autocomplete
            _commandField.KeyDown += (s, e) => 
            {
                // Skip if already handled by OnCommandKeyDown
                if (e.Handled)
                    return;
                    
                // Skip navigation keys when autocomplete is visible
                if (_autocompleteFrame!.Visible)
                {
                    if (e == Key.CursorDown || e == Key.CursorUp || e == Key.Tab || e == Key.Esc || e == Key.Enter)
                        return;
                }
                
                // Only trigger text change for actual text input keys
                Application.AddTimeout(TimeSpan.FromMilliseconds(50), () => 
                {
                    OnCommandTextChanged();
                    return false;
                });
            };

            // Global ESC and Ctrl+C handler
            top.KeyDown += (s, e) =>
            {
                if (e == Key.Esc)
                {
                    if (_autocompleteFrame!.Visible)
                    {
                        HideAutocomplete();
                        e.Handled = true;
                    }
                    else if (_isProcessing)
                    {
                        OnCancelClicked();
                        e.Handled = true;
                    }
                }
                // Ctrl+C to cancel current operation
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
            top.Add(_autocompleteFrame);

            _commandField.SetFocus();
        }
        
        private void OnCommandTextChanged()
        {
            try
            {
                var text = _commandField!.Text ?? "";
                var lastAtIndex = text.LastIndexOf('@');
                
                if (lastAtIndex >= 0)
                {
                    var textAfterAt = text.Substring(lastAtIndex + 1);
                    var spaceIndex = textAfterAt.IndexOfAny(new[] { ' ', '\n', '\r', '\t' });
                    if (spaceIndex < 0)
                    {
                        _autocompletePrefix = textAfterAt;
                        _autocompleteStartPos = lastAtIndex;
                        ShowFileAutocomplete(_autocompletePrefix);
                        return;
                    }
                }
                
                HideAutocomplete();
            }
            catch
            {
                HideAutocomplete();
            }
        }
        
        private void ShowFileAutocomplete(string prefix)
        {
            try
            {
                var searchDir = Directory.GetCurrentDirectory();
                var items = new List<string>();
                var searchPattern = string.IsNullOrEmpty(prefix) ? "*" : $"*{prefix}*";
                
                var excludeDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
                { "bin", "obj", "node_modules", ".git", ".vs", ".idea", "packages" };
                
                foreach (var dir in Directory.GetDirectories(searchDir, searchPattern, SearchOption.TopDirectoryOnly)
                    .Where(d => !excludeDirs.Contains(Path.GetFileName(d))).Take(8))
                {
                    var name = Path.GetFileName(dir);
                    if (!name.StartsWith("."))
                        items.Add($"[D] {name}/");
                }
                
                var excludeExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
                { ".dll", ".exe", ".pdb", ".cache", ".lock" };
                
                foreach (var file in Directory.GetFiles(searchDir, searchPattern, SearchOption.TopDirectoryOnly)
                    .Where(f => !excludeExts.Contains(Path.GetExtension(f))).Take(12))
                {
                    var name = Path.GetFileName(file);
                    if (!name.StartsWith("."))
                        items.Add($"[F] {name}");
                }
                
                if (items.Count > 0)
                {
                    Application.Invoke(() =>
                    {
                        _autocompleteItems.Clear();
                        foreach (var item in items)
                            _autocompleteItems.Add(item);
                        
                        _autocompleteList!.SelectedItem = 0;
                        _autocompleteFrame!.Visible = true;
                        _autocompleteFrame.SetNeedsDraw();
                        _autocompleteList.SetNeedsDraw();
                    });
                }
                else
                {
                    HideAutocomplete();
                }
            }
            catch
            {
                HideAutocomplete();
            }
        }
        
        private void HideAutocomplete()
        {
            Application.Invoke(() =>
            {
                _autocompleteFrame!.Visible = false;
                _autocompleteFrame.SetNeedsDraw();
            });
        }
        
        private void OnAutocompleteSelected(object? sender, ListViewItemEventArgs args)
        {
            if (args.Value is string selected)
            {
                var parts = selected.Split(' ', 2);
                var fileName = parts.Length > 1 ? parts[1].TrimEnd('/') : selected;
                
                var text = _commandField!.Text ?? "";
                var before = text.Substring(0, _autocompleteStartPos);
                var after = _autocompleteStartPos + 1 + _autocompletePrefix.Length <= text.Length 
                    ? text.Substring(_autocompleteStartPos + 1 + _autocompletePrefix.Length) 
                    : "";
                var newText = before + "@" + fileName + " " + after;
                
                _commandField.Text = newText;
                
                HideAutocomplete();
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
            // UI updates - must be called from UI thread or via Application.Invoke
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
            var status = _isProcessing ? " | PROCESSING (ESC=cancel)" : "";
            return $"Model: {AgentConfig.Config.Model} | Host: {AgentConfig.Config.HostUrl} | " +
                   $"Stream: {(AgentConfig.Config.Stream ? "ON" : "OFF")} | Msgs: {_messages.Count}{status}";
        }

        private void UpdateStatus()
        {
            // Must be called from UI thread or wrapped in Application.Invoke
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
            if (e == Key.Tab && _autocompleteFrame!.Visible)
            {
                e.Handled = true;
                var selected = _autocompleteList!.SelectedItem;
                if (selected >= 0 && selected < _autocompleteItems.Count)
                {
                    OnAutocompleteSelected(null, new ListViewItemEventArgs(selected, _autocompleteItems[selected]));
                }
                return;
            }
            
            // Arrow keys for autocomplete navigation
            if (_autocompleteFrame!.Visible)
            {
                if (e == Key.CursorDown)
                {
                    e.Handled = true;
                    if (_autocompleteList!.SelectedItem < _autocompleteItems.Count - 1)
                    {
                        _autocompleteList.SelectedItem++;
                        _autocompleteList.SetNeedsDraw();
                    }
                    return;
                }
                if (e == Key.CursorUp)
                {
                    e.Handled = true;
                    if (_autocompleteList!.SelectedItem > 0)
                    {
                        _autocompleteList.SelectedItem--;
                        _autocompleteList.SetNeedsDraw();
                    }
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

            HideAutocomplete();

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
@             File autocomplete
Tab           Select autocomplete
Esc           Close autocomplete / Cancel

/help         Show this help
/exit         Quit
/clear        Reset conversation
/stream on|off Toggle streaming
/models list  List models
/models use   Switch model

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
    }
}
