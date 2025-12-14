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
        private TextView? _commandField;
        private Button? _sendButton;
        private Button? _cancelButton;
        private ListView? _autocompleteList;
        private FrameView? _autocompleteFrame;
        private List<string> _autocompleteItems = new();
        private string _autocompletePrefix = "";
        private int _autocompleteStartPos = 0;
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
            string banner = 
                "╔══════════════════════════════════════════════════════════════╗\n"+
                "║  ████████╗██╗  ██╗██╗   ██╗██╗   ██╗██╗   ██╗                ║\n"+
                "║  ╚══██╔══╝██║  ██║██║   ██║██║   ██║██║   ██║                ║\n"+
                "║     ██║   ███████║██║   ██║██║   ██║██║   ██║                ║\n"+
                "║     ██║   ██╔══██║██║   ██║╚██╗ ██╔╝██║   ██║                ║\n"+
                "║     ██║   ██║  ██║╚██████╔╝ ╚████╔╝ ╚██████╔╝                ║\n"+
                "║     ╚═╝   ╚═╝  ╚═╝ ╚═════╝   ╚═══╝   ╚═════╝                 ║\n"+
                "╚══════════════════════════════════════════════════════════════╝\n";
            // Action area (middle) - scrollable text view  
            _actionView = new TextView
            {
                X = 0,
                Y = 2,
                Width = Dim.Fill(),
                Height = Dim.Fill() - 7, // Make room for 4-line input + labels
                ReadOnly = true,
                WordWrap = true,
                ColorScheme = new ColorScheme
                {
                    Normal = new Terminal.Gui.Attribute(Color.White, Color.Black),
                    Focus = new Terminal.Gui.Attribute(Color.BrightYellow, Color.Black)
                },
                Text = banner+"╭─── Welcome to T.H.U.V.U. ───────────────────────────────────────╮\n│  Type commands or chat with the AI assistant.                   │\n│  Press Ctrl+Enter to send. Type @ for file autocomplete.        │\n│  Type /help for available commands.                             │\n╰─────────────────────────────────────────────────────────────────╯\n\n"
            };

            // Command area (bottom)
            _commandLabel = new Label("Command (Ctrl+Enter to send, @ for files): ")
            {
                X = 0,
                Y = Pos.Bottom(_actionView),
                ColorScheme = new ColorScheme
                {
                    Normal = new Terminal.Gui.Attribute(Color.DarkGray, Color.Black)
                }
            };
            _workLabel = new Label(" ")
            {
                X = Pos.Right(_commandLabel),
                Y = Pos.Bottom(_actionView),
                Width = 30,
                Height = 1,
                ColorScheme = new ColorScheme
                {
                    Normal = new Terminal.Gui.Attribute(Color.Cyan, Color.Black)
                }
            };
            
            // Multi-line command input (4 lines)
            _commandField = new TextView
            {
                X = 0,
                Y = Pos.Bottom(_actionView) + 1,
                Width = Dim.Fill() - 12,
                Height = 4,
                WordWrap = true,
                ColorScheme = new ColorScheme
                {
                    Normal = new Terminal.Gui.Attribute(Color.BrightYellow, Color.Black),
                    Focus = new Terminal.Gui.Attribute(Color.BrightYellow, Color.DarkGray)
                }
            };

            _sendButton = new Button("Send")
            {
                X = Pos.Right(_commandField) + 1,
                Y = Pos.Bottom(_actionView) + 1,
                IsDefault = false
            };

            _cancelButton = new Button("Cancel")
            {
                X = Pos.Right(_commandField) + 1,
                Y = Pos.Bottom(_actionView) + 2,
                Visible = false // Hidden by default, shown during processing
            };
            
            // Autocomplete popup (hidden by default) - use absolute position near middle of screen
            _autocompleteFrame = new FrameView("📁 Files (Tab=select, ↑↓=navigate, Esc=close)")
            {
                X = 2,
                Y = 10,  // Fixed position from top
                Width = 60,
                Height = 12,
                Visible = false,
                ColorScheme = new ColorScheme
                {
                    Normal = new Terminal.Gui.Attribute(Color.Black, Color.Gray),
                    Focus = new Terminal.Gui.Attribute(Color.Black, Color.Gray)
                }
            };
            
            _autocompleteList = new ListView
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill(),
                CanFocus = true,
                ColorScheme = new ColorScheme
                {
                    Normal = new Terminal.Gui.Attribute(Color.Black, Color.Gray),
                    Focus = new Terminal.Gui.Attribute(Color.White, Color.Blue)
                }
            };
            _autocompleteFrame.Add(_autocompleteList);
            
            _autocompleteList.OpenSelectedItem += OnAutocompleteSelected;

            // Event handlers
            _sendButton.Clicked += OnSendClicked;
            _cancelButton.Clicked += OnCancelClicked;
            _commandField.KeyDown += OnCommandKeyDown;
            
            // Use KeyPress to detect @ character for autocomplete
            _commandField.KeyPress += (e) => 
            {
                // Skip autocomplete refresh for navigation keys when autocomplete is visible
                if (_autocompleteFrame!.Visible)
                {
                    if (e.KeyEvent.Key == Key.CursorDown || e.KeyEvent.Key == Key.CursorUp ||
                        e.KeyEvent.Key == Key.Tab || e.KeyEvent.Key == Key.Esc)
                    {
                        return; // Don't refresh autocomplete for these keys
                    }
                }
                
                // Delay the check slightly to allow the character to be inserted
                Application.MainLoop.AddTimeout(TimeSpan.FromMilliseconds(50), (_) => 
                {
                    OnCommandTextChanged();
                    return false; // Don't repeat
                });
            };

            // Global key handler for ESC to cancel or close autocomplete
            Application.Top.KeyDown += (e) =>
            {
                if (e.KeyEvent.Key == Key.Esc)
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
            };

            // Add components to Application.Top
            Application.Top.Add(_statusLabel);
            Application.Top.Add(_actionView);
            Application.Top.Add(_commandLabel);
            Application.Top.Add(_workLabel);
            Application.Top.Add(_commandField);
            Application.Top.Add(_sendButton);
            Application.Top.Add(_cancelButton);
            Application.Top.Add(_autocompleteFrame);

            // Set focus to command field
            _commandField.SetFocus();
        }
        
        private void OnCommandTextChanged()
        {
            try
            {
                var text = _commandField!.Text.ToString() ?? "";
                
                // Simple approach: just look for @ in the text
                var lastAtIndex = text.LastIndexOf('@');
                
                if (lastAtIndex >= 0)
                {
                    // Get text after @
                    var textAfterAt = text.Substring(lastAtIndex + 1);
                    
                    // If there's no space after @, show autocomplete
                    var spaceIndex = textAfterAt.IndexOfAny(new[] { ' ', '\n', '\r', '\t' });
                    if (spaceIndex < 0)
                    {
                        // No space found - show autocomplete with the prefix
                        _autocompletePrefix = textAfterAt;
                        _autocompleteStartPos = lastAtIndex;
                        ShowFileAutocomplete(_autocompletePrefix);
                        return;
                    }
                }
                
                HideAutocomplete();
            }
            catch (Exception ex)
            {
                // Log error to action view for debugging
                AppendActionText($"Autocomplete error: {ex.Message}", true);
                HideAutocomplete();
            }
        }
        
        private void ShowFileAutocomplete(string prefix)
        {
            try
            {
                // Search in current directory (project root) not just work directory
                var searchDir = Directory.GetCurrentDirectory();
                
                // Get files and directories
                var items = new List<string>();
                
                // If prefix is empty, show all files in current dir
                // If prefix has content, search for matching files
                var searchPattern = string.IsNullOrEmpty(prefix) ? "*" : $"*{prefix}*";
                
                // Add directories first (exclude hidden and common non-useful dirs)
                var excludeDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
                { 
                    "bin", "obj", "node_modules", ".git", ".vs", ".idea", "packages" 
                };
                
                foreach (var dir in Directory.GetDirectories(searchDir, searchPattern, SearchOption.TopDirectoryOnly)
                    .Where(d => !excludeDirs.Contains(Path.GetFileName(d)))
                    .Take(8))
                {
                    var name = Path.GetFileName(dir);
                    if (!name.StartsWith("."))
                    {
                        items.Add($"📁 {name}/");
                    }
                }
                
                // Add files from current directory
                var excludeExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
                { 
                    ".dll", ".exe", ".pdb", ".cache", ".lock" 
                };
                
                foreach (var file in Directory.GetFiles(searchDir, searchPattern, SearchOption.TopDirectoryOnly)
                    .Where(f => !excludeExts.Contains(Path.GetExtension(f)))
                    .Take(12))
                {
                    var name = Path.GetFileName(file);
                    if (!name.StartsWith("."))
                    {
                        var icon = GetFileIcon(name);
                        items.Add($"{icon} {name}");
                    }
                }
                
                // Also search in subdirectories if we need more results or prefix is specific
                if ((items.Count < 8 || !string.IsNullOrEmpty(prefix)) && prefix.Length > 0)
                {
                    try
                    {
                        foreach (var file in Directory.GetFiles(searchDir, searchPattern, SearchOption.AllDirectories)
                            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                                       !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                                       !f.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}") &&
                                       !excludeExts.Contains(Path.GetExtension(f)))
                            .Take(15))
                        {
                            var relativePath = Path.GetRelativePath(searchDir, file);
                            if (!items.Any(i => i.EndsWith(Path.GetFileName(file))))
                            {
                                var icon = GetFileIcon(relativePath);
                                items.Add($"{icon} {relativePath}");
                            }
                            if (items.Count >= 15) break;
                        }
                    }
                    catch { /* Ignore recursive search errors */ }
                }
                
                if (items.Count > 0)
                {
                    _autocompleteItems = items;
                    Application.MainLoop.Invoke(() =>
                    {
                        _autocompleteList!.SetSource(_autocompleteItems);
                        _autocompleteList.SelectedItem = 0;
                        _autocompleteFrame!.Visible = true;
                        
                        // Bring to front by removing and re-adding
                        Application.Top.Remove(_autocompleteFrame);
                        Application.Top.Add(_autocompleteFrame);
                        
                        _autocompleteFrame.SetNeedsDisplay();
                        _autocompleteList.SetNeedsDisplay();
                        Application.Refresh();
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
        
        private string GetFileIcon(string fileName)
        {
            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            return ext switch
            {
                ".cs" => "🔷",
                ".csproj" or ".sln" => "🔶",
                ".json" => "📋",
                ".xml" => "📄",
                ".md" => "📝",
                ".txt" => "📃",
                ".ts" or ".js" => "🟨",
                ".py" => "🐍",
                ".sh" or ".ps1" => "⚙️",
                ".yaml" or ".yml" => "📑",
                ".html" or ".css" => "🌐",
                ".sql" => "🗃️",
                _ => "📄"
            };
        }
        
        private void HideAutocomplete()
        {
            Application.MainLoop.Invoke(() =>
            {
                _autocompleteFrame!.Visible = false;
                _autocompleteFrame.SetNeedsDisplay();
            });
        }
        
        private void OnAutocompleteSelected(ListViewItemEventArgs args)
        {
            if (args.Value is string selected)
            {
                // Extract the filename (remove icon prefix)
                var parts = selected.Split(' ', 2);
                var fileName = parts.Length > 1 ? parts[1].TrimEnd('/') : selected;
                
                // Replace @prefix with the selected file
                var text = _commandField!.Text.ToString();
                var before = text.Substring(0, _autocompleteStartPos);
                var after = text.Substring(_autocompleteStartPos + 1 + _autocompletePrefix.Length);
                var newText = before + "@" + fileName + " " + after;
                
                _commandField.Text = newText;
                _commandField.CursorPosition = new Point(_autocompleteStartPos + fileName.Length + 2, 0);
                
                HideAutocomplete();
                _commandField.SetFocus();
            }
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
            var streamIcon = AgentConfig.Config.Stream ? "◉" : "○";
            var status = _isProcessing ? " │ ⏳ PROCESSING (ESC to cancel)" : "";
            return $"🤖 {AgentConfig.Config.Model} │ 🌐 {AgentConfig.Config.HostUrl} │ " +
                   $"{streamIcon} Stream: {(AgentConfig.Config.Stream ? "ON" : "OFF")} │ " +
                   $"💬 {_messages.Count} msgs{status}";
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
                
                // Add styled prefix and timestamp
                string styledText;
                if (isError)
                {
                    styledText = $"╭─ ✗ ERROR [{DateTime.Now:HH:mm:ss}] ─────────────────────────────────────────\n│ {text}\n╰───────────────────────────────────────────────────────────────────";
                }
                else
                {
                    styledText = text;
                }

                _actionView.Text = currentText + styledText + "\n";
                
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
                _actionView.Text = currentText + $"  ✓ {text}\n";
                
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
                _actionView.Text = currentText + $"  🔧 TOOL │ {text}\n";
                
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
                _actionView.Text = currentText + $"  🎫 TOKENS │ {text}\n";
                
                // Scroll to bottom
                _actionView.MoveEnd();
                _actionView.SetNeedsDisplay();
            });
        }

        private void OnCommandKeyDown(View.KeyEventEventArgs e)
        {
            // Ctrl+Enter to send message
            if (e.KeyEvent.Key == (Key.Enter | Key.CtrlMask))
            {
                e.Handled = true;
                _ = ProcessCommandAsync();
                return;
            }
            
            // Tab to select autocomplete item
            if (e.KeyEvent.Key == Key.Tab && _autocompleteFrame!.Visible)
            {
                e.Handled = true;
                var selected = _autocompleteList!.SelectedItem;
                if (selected >= 0 && selected < _autocompleteItems.Count)
                {
                    OnAutocompleteSelected(new ListViewItemEventArgs(selected, _autocompleteItems[selected]));
                }
                return;
            }
            
            // Arrow keys to navigate autocomplete
            if (_autocompleteFrame!.Visible)
            {
                if (e.KeyEvent.Key == Key.CursorDown)
                {
                    e.Handled = true;
                    if (_autocompleteList!.SelectedItem < _autocompleteItems.Count - 1)
                    {
                        _autocompleteList.SelectedItem++;
                        _autocompleteList.SetNeedsDisplay();
                    }
                    return;
                }
                if (e.KeyEvent.Key == Key.CursorUp)
                {
                    e.Handled = true;
                    if (_autocompleteList!.SelectedItem > 0)
                    {
                        _autocompleteList.SelectedItem--;
                        _autocompleteList.SetNeedsDisplay();
                    }
                    return;
                }
            }
        }

        private void OnSendClicked()
        {
            _ = ProcessCommandAsync();
        }

        private async Task ProcessCommandAsync()
        {
            var command = _commandField!.Text.ToString().Trim().Replace("\r\n", " ").Replace("\n", " ");
            if (string.IsNullOrWhiteSpace(command))
                return;

            // Hide autocomplete if visible
            HideAutocomplete();

            // Clear the command field
            Application.MainLoop.Invoke(() =>
            {
                _commandField.Text = "";
                _commandField.SetNeedsDisplay();
            });

            // Display the user command with styled prefix
            AppendActionText($"👤 ➤ {command}");

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

                AppendActionText($"╭─── 📤 Sending to {AgentConfig.Config.Model} ───────────────────────────────────\n│ Press ESC to cancel");
                bool finished = false;
                int iterationCount = 0;
                try
                {
                    while (!finished && !ct.IsCancellationRequested)
                    {
                        iterationCount++;
                        AppendActionText($"╭─ 🔄 Iteration {iterationCount} [{DateTime.Now:HH:mm:ss}] ──────────────────────────────────");
                        
                        string? final;
                        if (AgentConfig.Config.Stream)
                        {
                            // Track streaming state
                            bool receivedTokens = false;
                            var tokenBuffer = new System.Text.StringBuilder();
                            var streamStartTime = DateTime.Now;
                            int tokenCount = 0;
                            
                            // Start thinking animation
                            var thinkingCts = new CancellationTokenSource();
                            var thinkingChars = new[] { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };
                            int thinkingIdx = 0;
                            _ = Task.Run(async () =>
                            {
                                while (!thinkingCts.Token.IsCancellationRequested && !receivedTokens)
                                {
                                    var elapsed = (DateTime.Now - streamStartTime).TotalSeconds;
                                    Application.MainLoop.Invoke(() =>
                                    {
                                        _workLabel!.Text = $"{thinkingChars[thinkingIdx % thinkingChars.Length]} {elapsed:F0}s";
                                        _workLabel.SetNeedsDisplay();
                                    });
                                    thinkingIdx++;
                                    await Task.Delay(100, thinkingCts.Token);
                                }
                            }, thinkingCts.Token);
                            
                            final = await AgentLoop.CompleteWithToolsStreamingAsync(
                                _http, AgentConfig.Config.Model, _messages, _tools, ct,
                                onToken: token => 
                                {
                                    if (!receivedTokens)
                                    {
                                        receivedTokens = true;
                                        thinkingCts.Cancel();
                                        Application.MainLoop.Invoke(() =>
                                        {
                                            var currentText = _actionView!.Text.ToString();
                                            var elapsed = (DateTime.Now - streamStartTime).TotalSeconds;
                                            _actionView.Text = currentText + $"╭─── 🤖 Response (first token in {elapsed:F1}s) ────────────────────────────────\n│ ";
                                            _actionView.MoveEnd();
                                            _actionView.SetNeedsDisplay();
                                            Application.Refresh();
                                        });
                                    }
                                    
                                    tokenCount++;
                                    tokenBuffer.Append(token);
                                    
                                    // Update work label with token count
                                    if (tokenCount % 5 == 0)
                                    {
                                        var elapsed = (DateTime.Now - streamStartTime).TotalSeconds;
                                        var tps = elapsed > 0 ? tokenCount / elapsed : 0;
                                        Application.MainLoop.Invoke(() =>
                                        {
                                            _workLabel!.Text = $"📝 {tokenCount} ({tps:F0} t/s)";
                                            _workLabel.SetNeedsDisplay();
                                        });
                                    }
                                    
                                    // Buffer tokens and flush periodically for better performance
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
                                    thinkingCts.Cancel();
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
                                    var elapsed = (DateTime.Now - streamStartTime).TotalSeconds;
                                    var tps = elapsed > 0 ? tokenCount / elapsed : 0;
                                    AppendActionText($"╰─── ⏱ {elapsed:F1}s │ 📝 {tokenCount} tokens │ ⚡ {tps:F1} t/s ───────────────────────");
                                    AppendTokenText($"prompt={usage.PromptTokens}, completion={usage.CompletionTokens}, total={usage.TotalTokens}");
                                    Application.MainLoop.Invoke(() =>
                                    {
                                        _workLabel!.Text = " ";
                                        _workLabel.SetNeedsDisplay();
                                    });
                                    Animate();
                                }
                            );
                            
                            thinkingCts.Cancel();
                            
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
            var helpText = @"╔══════════════════════════════════════════════════════════════════════════╗
║                    T.H.U.V.U. HELP                                       ║
║   (T)ool for (H)eurustic (U)niversal (V)ersatile (U)sage                 ║
╠══════════════════════════════════════════════════════════════════════════╣
║  KEYBOARD SHORTCUTS                                                      ║
╠══════════════════════════════════════════════════════════════════════════╣
║  Ctrl+Enter               │ Send message                                 ║
║  @                        │ File autocomplete (type @ then filename)     ║
║  Tab                      │ Select autocomplete item                     ║
║  ↑/↓                      │ Navigate autocomplete list                   ║
║  Esc                      │ Close autocomplete / Cancel request          ║
╠══════════════════════════════════════════════════════════════════════════╣
║  BASIC COMMANDS                                                          ║
╠══════════════════════════════════════════════════════════════════════════╣
║  /help                    │ Show this help                               ║
║  /exit                    │ Quit                                         ║
║  /clear                   │ Reset conversation                           ║
║  /system <text>           │ Set system prompt                            ║
║  /stream on|off           │ Toggle streaming                             ║
╠══════════════════════════════════════════════════════════════════════════╣
║  DEVELOPMENT COMMANDS                                                    ║
╠══════════════════════════════════════════════════════════════════════════╣
║  /diff [--staged]         │ Show git unified diff                        ║
║  /test [PROJECT]          │ Run dotnet tests                             ║
║  /run CMD [ARGS]          │ Run whitelisted command                      ║
╠══════════════════════════════════════════════════════════════════════════╣
║  PERMISSION SYSTEM                                                       ║
╠══════════════════════════════════════════════════════════════════════════╣
║  Read-only tools: always allowed                                         ║
║  Write tools prompt: [A]lways │ [S]ession │ [O]nce │ [N]o                ║
╚══════════════════════════════════════════════════════════════════════════╝";

            AppendActionText(helpText);
        }
    }
}
