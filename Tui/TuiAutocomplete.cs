using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Terminal.Gui;

namespace thuvu.Tui
{
    /// <summary>
    /// Handles command and file autocomplete for the TUI
    /// </summary>
    public class TuiAutocomplete
    {
        private readonly FrameView _autocompleteFrame;
        private readonly ListView _autocompleteList;
        private readonly ObservableCollection<string> _autocompleteItems = new();
        
        private string _autocompletePrefix = "";
        private int _autocompleteStartPos = 0;
        private bool _isCommandAutocomplete = false;
        
        public static readonly string[] AvailableCommands = new[]
        {
            "/help", "/exit", "/clear", "/stream", "/models", "/config", "/set",
            "/diff", "/test", "/run", "/commit", "/push", "/pull",
            "/rag", "/mcp", "/plan", "/orchestrate", "/health", "/status", "/tokens", "/summarize"
        };
        
        public bool IsVisible => _autocompleteFrame.Visible;
        public bool IsCommandAutocomplete => _isCommandAutocomplete;
        public string Prefix => _autocompletePrefix;
        public int StartPos => _autocompleteStartPos;
        
        public TuiAutocomplete()
        {
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
                Source = new ListWrapper<string>(_autocompleteItems),
                ColorScheme = new ColorScheme
                {
                    Normal = new Terminal.Gui.Attribute(Color.Black, Color.Gray),
                    Focus = new Terminal.Gui.Attribute(Color.White, Color.Blue)
                }
            };
            _autocompleteFrame.Add(_autocompleteList);
        }
        
        public FrameView Frame => _autocompleteFrame;
        public ListView List => _autocompleteList;
        public ObservableCollection<string> Items => _autocompleteItems;
        
        /// <summary>
        /// Process text change and show appropriate autocomplete
        /// </summary>
        public void ProcessTextChange(string text)
        {
            try
            {
                var lastAtIndex = text.LastIndexOf('@');
                
                // Check for command autocomplete (starts with /)
                if (text.StartsWith("/"))
                {
                    var spaceIndex = text.IndexOf(' ');
                    if (spaceIndex < 0)
                    {
                        _autocompletePrefix = text.Substring(1);
                        _autocompleteStartPos = 0;
                        _isCommandAutocomplete = true;
                        ShowCommandAutocomplete(_autocompletePrefix);
                        return;
                    }
                }
                
                // Check for file autocomplete (@)
                if (lastAtIndex >= 0)
                {
                    var textAfterAt = text.Substring(lastAtIndex + 1);
                    var spaceIndex = textAfterAt.IndexOfAny(new[] { ' ', '\n', '\r', '\t' });
                    if (spaceIndex < 0)
                    {
                        _autocompletePrefix = textAfterAt;
                        _autocompleteStartPos = lastAtIndex;
                        _isCommandAutocomplete = false;
                        ShowFileAutocomplete(_autocompletePrefix);
                        return;
                    }
                }
                
                Hide();
            }
            catch
            {
                Hide();
            }
        }
        
        private void ShowCommandAutocomplete(string prefix)
        {
            try
            {
                var items = AvailableCommands
                    .Where(c => string.IsNullOrEmpty(prefix) || c.Contains(prefix, StringComparison.OrdinalIgnoreCase))
                    .Take(15)
                    .ToList();
                
                ShowItems(items);
            }
            catch
            {
                Hide();
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
                        items.Add($"dir:{name}/");
                }
                
                var excludeExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) 
                { ".dll", ".exe", ".pdb", ".cache", ".lock" };
                
                foreach (var file in Directory.GetFiles(searchDir, searchPattern, SearchOption.TopDirectoryOnly)
                    .Where(f => !excludeExts.Contains(Path.GetExtension(f))).Take(12))
                {
                    var name = Path.GetFileName(file);
                    if (!name.StartsWith("."))
                        items.Add($"file:{name}");
                }
                
                ShowItems(items);
            }
            catch
            {
                Hide();
            }
        }
        
        private void ShowItems(List<string> items)
        {
            if (items.Count > 0)
            {
                Application.Invoke(() =>
                {
                    _autocompleteItems.Clear();
                    foreach (var item in items)
                        _autocompleteItems.Add(item);
                    
                    _autocompleteList.SelectedItem = 0;
                    _autocompleteFrame.Visible = true;
                    _autocompleteFrame.SetNeedsDraw();
                    _autocompleteList.SetNeedsDraw();
                });
            }
            else
            {
                Hide();
            }
        }
        
        public void Hide()
        {
            Application.Invoke(() =>
            {
                _autocompleteFrame.Visible = false;
                _autocompleteFrame.SetNeedsDraw();
            });
        }
        
        /// <summary>
        /// Get the selected item text
        /// </summary>
        public string? GetSelectedItem()
        {
            var selected = _autocompleteList.SelectedItem;
            if (selected >= 0 && selected < _autocompleteItems.Count)
                return _autocompleteItems[selected];
            return null;
        }
        
        /// <summary>
        /// Apply the selected autocomplete item to the text
        /// </summary>
        public string ApplySelection(string currentText, string selectedItem)
        {
            if (_isCommandAutocomplete)
            {
                // Command autocomplete - replace the whole command
                return selectedItem + " ";
            }
            else
            {
                // File autocomplete - keep the selected item as-is (file: or dir: prefix included)
                var before = currentText.Substring(0, _autocompleteStartPos);
                var afterPos = _autocompleteStartPos + 1 + _autocompletePrefix.Length;
                var after = afterPos <= currentText.Length ? currentText.Substring(afterPos) : "";
                
                // Remove trailing slash for directories in the replacement
                var fileRef = selectedItem.TrimEnd('/');
                return before + fileRef + " " + after;
            }
        }
        
        /// <summary>
        /// Navigate autocomplete list up
        /// </summary>
        public void MoveUp()
        {
            if (_autocompleteList.SelectedItem > 0)
            {
                _autocompleteList.SelectedItem--;
                _autocompleteList.SetNeedsDraw();
            }
        }
        
        /// <summary>
        /// Navigate autocomplete list down
        /// </summary>
        public void MoveDown()
        {
            if (_autocompleteList.SelectedItem < _autocompleteItems.Count - 1)
            {
                _autocompleteList.SelectedItem++;
                _autocompleteList.SetNeedsDraw();
            }
        }
        
        /// <summary>
        /// Reset autocomplete state
        /// </summary>
        public void Reset()
        {
            _autocompletePrefix = "";
            _autocompleteStartPos = 0;
            _isCommandAutocomplete = false;
        }
    }
}
