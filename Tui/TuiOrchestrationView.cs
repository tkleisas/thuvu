using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Terminal.Gui;
using thuvu.Models;
using TgAttribute = Terminal.Gui.Attribute;

namespace thuvu.Tui
{
    /// <summary>
    /// Manages orchestration mode UI with multi-panel layout
    /// </summary>
    public class TuiOrchestrationView : IDisposable
    {
        private FrameView? _orchestratorFrame;
        private TextView? _orchestratorView;
        private FrameView? _agentOutputFrame;
        private TabView? _agentTabView;
        private Dictionary<string, (Tab tab, TextView view)> _agentOutputViews = new();
        private volatile bool _orchestrationMode = false;
        private volatile bool _isExiting = false; // Prevents new output during exit
        private readonly Toplevel _top;
        private readonly object _lock = new();
        
        // Output batching for better performance
        private ConcurrentDictionary<string, StringBuilder> _agentOutputBuffers = new();
        private StringBuilder _orchestratorBuffer = new();
        private Timer? _flushTimer;
        private const int FlushIntervalMs = 100; // Batch updates every 100ms
        
        public bool IsOrchestrationMode => _orchestrationMode && !_isExiting;
        
        public TuiOrchestrationView(Toplevel top)
        {
            _top = top;
        }
        
        /// <summary>
        /// Switch to orchestration mode with multi-panel layout
        /// </summary>
        public void Enter(int agentCount, View actionView, Label commandLabel, Label workLabel, TextView commandField, Button sendButton, Button cancelButton)
        {
            if (_orchestrationMode) return;
            _orchestrationMode = true;
            
            Application.Invoke(() =>
            {
                try
                {
                    // Calculate dimensions
                    var totalHeight = Application.Top!.Frame.Height;
                    var orchestratorHeight = Math.Max(6, totalHeight / 4);
                    var inputHeight = 5;
                    var agentHeight = totalHeight - orchestratorHeight - inputHeight - 2;
                    
                    // Hide normal action view
                    actionView.Visible = false;
                    
                    // Create orchestrator frame
                    _orchestratorFrame = new FrameView
                    {
                        X = 0,
                        Y = 1,
                        Width = Dim.Fill(),
                        Height = orchestratorHeight,
                        Title = "Orchestrator Status",
                        ColorScheme = new ColorScheme
                        {
                            Normal = new TgAttribute(Color.Cyan, Color.Black),
                            Focus = new TgAttribute(Color.Cyan, Color.Black)
                        }
                    };
                    
                    _orchestratorView = new TextView
                    {
                        X = 0,
                        Y = 0,
                        Width = Dim.Fill(),
                        Height = Dim.Fill(),
                        ReadOnly = true,
                        WordWrap = true,
                        ColorScheme = new ColorScheme
                        {
                            Normal = new TgAttribute(Color.White, Color.Black),
                            Focus = new TgAttribute(Color.White, Color.Black)
                        }
                    };
                    _orchestratorFrame.Add(_orchestratorView);
                    
                    // Create agent output frame with tabs
                    _agentOutputFrame = new FrameView
                    {
                        X = 0,
                        Y = Pos.Bottom(_orchestratorFrame),
                        Width = Dim.Fill(),
                        Height = agentHeight,
                        Title = "Agent Output",
                        CanFocus = false, // Let children handle focus
                        ColorScheme = new ColorScheme
                        {
                            Normal = new TgAttribute(Color.Green, Color.Black),
                            Focus = new TgAttribute(Color.Green, Color.Black)
                        }
                    };
                    
                    _agentTabView = new TabView
                    {
                        X = 0,
                        Y = 0,
                        Width = Dim.Fill(),
                        Height = Dim.Fill(),
                        CanFocus = true,
                    };
                    
                    // Handle tab switching explicitly
                    _agentTabView.SelectedTabChanged += (sender, args) =>
                    {
                        // Ensure the selected tab's view gets focus
                        if (args.NewTab?.View != null)
                        {
                            args.NewTab.View.SetFocus();
                        }
                    };
                    
                    // Add keyboard shortcuts for tab switching (Ctrl+1, Ctrl+2, etc.)
                    _agentTabView.KeyDown += (sender, args) =>
                    {
                        if (args.KeyCode >= KeyCode.D1 && args.KeyCode <= KeyCode.D9)
                        {
                            var index = (int)args.KeyCode - (int)KeyCode.D1;
                            if (index < _agentTabView.Tabs.Count)
                            {
                                _agentTabView.SelectedTab = _agentTabView.Tabs.ElementAt(index);
                                args.Handled = true;
                            }
                        }
                    };
                    
                    // Create tabs for each agent
                    for (int i = 0; i < agentCount; i++)
                    {
                        var agentId = $"Agent-{i + 1}";
                        var agentView = new TextView
                        {
                            X = 0,
                            Y = 0,
                            Width = Dim.Fill(),
                            Height = Dim.Fill(),
                            ReadOnly = true,
                            WordWrap = true,
                            ColorScheme = new ColorScheme
                            {
                                Normal = new TgAttribute(Color.White, Color.Black),
                                Focus = new TgAttribute(Color.BrightYellow, Color.Black)
                            },
                            Text = $"=== {agentId} output ===\n"
                        };
                        
                        var tab = new Tab { DisplayText = agentId, View = agentView };
                        _agentTabView.AddTab(tab, i == 0);
                        _agentOutputViews[agentId] = (tab, agentView);
                    }
                    
                    _agentOutputFrame.Add(_agentTabView);
                    
                    // Move command area
                    commandLabel.Y = Pos.Bottom(_agentOutputFrame);
                    workLabel.Y = Pos.Bottom(_agentOutputFrame);
                    commandField.Y = Pos.Bottom(_agentOutputFrame) + 1;
                    sendButton.Y = Pos.Bottom(_agentOutputFrame) + 1;
                    cancelButton.Y = Pos.Bottom(_agentOutputFrame) + 2;
                    
                    // Add new frames to toplevel
                    _top.Add(_orchestratorFrame);
                    _top.Add(_agentOutputFrame);
                    
                    // Start output flush timer for batching
                    _flushTimer = new Timer(FlushOutputBuffers, null, FlushIntervalMs, FlushIntervalMs);
                    
                    _top.SetNeedsDraw();
                }
                catch (Exception ex)
                {
                    SessionLogger.Instance.LogError($"Failed to enter orchestration mode: {ex.Message}");
                }
            });
        }
        
        /// <summary>
        /// Flush buffered output to UI (called by timer)
        /// </summary>
        private void FlushOutputBuffers(object? state)
        {
            if (!_orchestrationMode || _isExiting) return;
            
            // Flush orchestrator buffer
            string orchestratorText;
            lock (_orchestratorBuffer)
            {
                if (_orchestratorBuffer.Length == 0) orchestratorText = "";
                else
                {
                    orchestratorText = _orchestratorBuffer.ToString();
                    _orchestratorBuffer.Clear();
                }
            }
            
            // Flush agent buffers
            var agentTexts = new Dictionary<string, string>();
            foreach (var kvp in _agentOutputBuffers)
            {
                lock (kvp.Value)
                {
                    if (kvp.Value.Length > 0)
                    {
                        agentTexts[kvp.Key] = kvp.Value.ToString();
                        kvp.Value.Clear();
                    }
                }
            }
            
            // Update UI if there's anything to flush
            if (orchestratorText.Length > 0 || agentTexts.Count > 0)
            {
                Application.Invoke(() =>
                {
                    if (!_orchestrationMode || _isExiting) return;
                    
                    try
                    {
                        // Update orchestrator view
                        if (orchestratorText.Length > 0 && _orchestratorView?.SuperView != null)
                        {
                            var currentText = _orchestratorView.Text ?? "";
                            _orchestratorView.Text = currentText + orchestratorText;
                            _orchestratorView.MoveEnd();
                        }
                        
                        // Update agent views
                        lock (_lock)
                        {
                            foreach (var kvp in agentTexts)
                            {
                                if (_agentOutputViews.TryGetValue(kvp.Key, out var tabInfo))
                                {
                                    if (tabInfo.view?.SuperView != null)
                                    {
                                        var currentText = tabInfo.view.Text ?? "";
                                        tabInfo.view.Text = currentText + kvp.Value;
                                        tabInfo.view.MoveEnd();
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        SessionLogger.Instance.LogError($"FlushOutputBuffers error: {ex.Message}");
                    }
                });
            }
        }
        
        /// <summary>
        /// Exit orchestration mode and return to normal layout
        /// </summary>
        public void Exit(View actionView, Label commandLabel, Label workLabel, TextView commandField, Button sendButton, Button cancelButton)
        {
            if (!_orchestrationMode) return;
            
            SessionLogger.Instance.LogInfo("Exiting orchestration mode...");
            
            // Set exiting flag first to stop all new output immediately
            _isExiting = true;
            
            // Stop the flush timer
            _flushTimer?.Dispose();
            _flushTimer = null;
            
            // Clear buffers
            _agentOutputBuffers.Clear();
            lock (_orchestratorBuffer) { _orchestratorBuffer.Clear(); }
            
            // Wait a bit for pending callbacks to see the flag
            Thread.Sleep(100);
            
            // Now set mode to false
            _orchestrationMode = false;
            
            // Use a delay to allow any pending agent callbacks to complete
            Application.AddTimeout(TimeSpan.FromMilliseconds(1500), () =>
            {
                lock (_lock)
                {
                    _agentOutputViews.Clear();
                }
                
                try
                {
                    if (_orchestratorFrame != null)
                    {
                        _top.Remove(_orchestratorFrame);
                        _orchestratorFrame.Dispose();
                        _orchestratorFrame = null;
                    }
                    
                    if (_agentOutputFrame != null)
                    {
                        _top.Remove(_agentOutputFrame);
                        _agentOutputFrame.Dispose();
                        _agentOutputFrame = null;
                    }
                    
                    _orchestratorView = null;
                    _agentTabView = null;
                    
                    // Show normal action view and reset layout
                    actionView.Visible = true;
                    if (actionView is TextView tv)
                        tv.Height = Dim.Fill() - 7;
                    
                    // Reset command area positions
                    commandLabel.Y = Pos.Bottom(actionView);
                    workLabel.Y = Pos.Bottom(actionView);
                    commandField.Y = Pos.Bottom(actionView) + 1;
                    commandField.Height = 4;
                    sendButton.Y = Pos.Bottom(actionView) + 1;
                    cancelButton.Y = Pos.Bottom(actionView) + 2;
                    
                    _top.SetNeedsLayout();
                    _top.SetNeedsDraw();
                    
                    commandField.SetFocus();
                    
                    // Reset exiting flag after cleanup is complete
                    _isExiting = false;
                    
                    SessionLogger.Instance.LogInfo("Exited orchestration mode successfully");
                }
                catch (Exception ex)
                {
                    SessionLogger.Instance.LogError($"Failed to exit orchestration mode: {ex.Message}");
                    _isExiting = false;
                }
                return false;
            });
        }
        
        /// <summary>
        /// Ensure an agent tab exists and return its view
        /// </summary>
        public TextView? GetOrCreateAgentView(string agentId)
        {
            lock (_lock)
            {
                if (_agentOutputViews.TryGetValue(agentId, out var existing))
                    return existing.view;
                
                if (_agentTabView == null) return null;
                
                // Create new tab for this agent
                var agentView = new TextView
                {
                    X = 0,
                    Y = 0,
                    Width = Dim.Fill(),
                    Height = Dim.Fill(),
                    ReadOnly = true,
                    WordWrap = true,
                    ColorScheme = new ColorScheme
                    {
                        Normal = new TgAttribute(Color.White, Color.Black),
                        Focus = new TgAttribute(Color.BrightYellow, Color.Black)
                    },
                    Text = $"=== {agentId} output ===\n"
                };
                
                var tab = new Tab { DisplayText = agentId, View = agentView };
                
                Application.Invoke(() =>
                {
                    _agentTabView?.AddTab(tab, false);
                });
                
                _agentOutputViews[agentId] = (tab, agentView);
                return agentView;
            }
        }
        
        /// <summary>
        /// Append text to agent-specific output view (buffered)
        /// </summary>
        public void AppendAgentOutput(string agentId, string text)
        {
            // Check both flags to prevent output during transitions
            if (!_orchestrationMode || _isExiting) return;
            
            // Skip carriage returns which can cause display issues
            if (text == "\r") return;
            text = text.Replace("\r", "");
            
            // Ensure agent view exists
            GetOrCreateAgentView(agentId);
            
            // Add to buffer - will be flushed by timer
            var buffer = _agentOutputBuffers.GetOrAdd(agentId, _ => new StringBuilder());
            lock (buffer)
            {
                buffer.Append(text);
            }
        }
        
        /// <summary>
        /// Append orchestrator status message (buffered)
        /// </summary>
        public void AppendOrchestratorStatus(string text, TextView? fallbackView = null)
        {
            // Allow orchestrator status even during exit (for final messages)
            if (_isExiting && fallbackView == null) return;
            
            // If we have a fallback view and we're exiting, update directly
            if (fallbackView != null && (_isExiting || !_orchestrationMode))
            {
                Application.Invoke(() =>
                {
                    try
                    {
                        if (fallbackView?.SuperView != null)
                        {
                            var currentText = fallbackView.Text ?? "";
                            fallbackView.Text = currentText + text + "\n";
                            fallbackView.MoveEnd();
                        }
                    }
                    catch { }
                });
                return;
            }
            
            // Add to buffer - will be flushed by timer
            lock (_orchestratorBuffer)
            {
                _orchestratorBuffer.Append(text);
                _orchestratorBuffer.Append('\n');
            }
        }
        
        public void Dispose()
        {
            _flushTimer?.Dispose();
            _flushTimer = null;
            _orchestratorFrame?.Dispose();
            _agentOutputFrame?.Dispose();
            _agentOutputViews.Clear();
            _agentOutputBuffers.Clear();
        }
    }
}
