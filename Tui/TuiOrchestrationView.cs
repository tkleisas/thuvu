using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Terminal.Gui.Views;
using Terminal.Gui.ViewBase;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using TgAttr = Terminal.Gui.Drawing.Attribute;
using thuvu.Models;

namespace thuvu.Tui
{
    public class TuiOrchestrationView : IDisposable
    {
        private FrameView? _orchestratorFrame;
        private TextView? _orchestratorView;
        private FrameView? _agentOutputFrame;
        private Tabs? _agentTabs;
        private readonly Dictionary<string, (View tabView, TextView textView)> _agentOutputViews = new();
        private volatile bool _orchestrationMode = false;
        private volatile bool _isExiting = false;
        private readonly View _top;
        private readonly object _lock = new();
        private int _tabIndex = 0;

        private readonly ConcurrentDictionary<string, StringBuilder> _agentOutputBuffers = new();
        private readonly StringBuilder _orchestratorBuffer = new();
        private Timer? _flushTimer;
        private const int FlushIntervalMs = 100;

        public bool IsOrchestrationMode => _orchestrationMode && !_isExiting;

        public TuiOrchestrationView(View top)
        {
            _top = top;
        }

        public void Enter(int agentCount, View actionView, Label commandLabel, Label workLabel, TextView commandField, Button sendButton, Button cancelButton)
        {
            if (_orchestrationMode) return;
            _orchestrationMode = true;

            Application.Invoke(() =>
            {
                try
                {
                    var totalHeight = _top.Frame.Height;
                    var orchestratorHeight = Math.Max(6, totalHeight / 4);
                    var inputHeight = 5;
                    var agentHeight = totalHeight - orchestratorHeight - inputHeight - 2;

                    actionView.Visible = false;

                    _orchestratorFrame = new FrameView
                    {
                        X = 0,
                        Y = 1,
                        Width = Dim.Fill(),
                        Height = orchestratorHeight,
                        Title = "Orchestrator Status"
                    };
                    _orchestratorFrame.SetScheme(new Scheme
                    {
                        Normal = new TgAttr(Color.Cyan, Color.Black),
                        Focus = new TgAttr(Color.Cyan, Color.Black)
                    });

                    _orchestratorView = new TextView
                    {
                        X = 0,
                        Y = 0,
                        Width = Dim.Fill(),
                        Height = Dim.Fill(),
                        ReadOnly = true,
                        WordWrap = true
                    };
                    _orchestratorView.SetScheme(new Scheme
                    {
                        Normal = new TgAttr(Color.White, Color.Black),
                        Focus = new TgAttr(Color.White, Color.Black)
                    });
                    _orchestratorFrame.Add(_orchestratorView);

                    _agentOutputFrame = new FrameView
                    {
                        X = 0,
                        Y = Pos.Bottom(_orchestratorFrame),
                        Width = Dim.Fill(),
                        Height = agentHeight,
                        Title = "Agent Output"
                    };
                    _agentOutputFrame.SetScheme(new Scheme
                    {
                        Normal = new TgAttr(Color.Green, Color.Black),
                        Focus = new TgAttr(Color.Green, Color.Black)
                    });

                    _agentTabs = new Tabs
                    {
                        X = 0,
                        Y = 0,
                        Width = Dim.Fill(),
                        Height = Dim.Fill(),
                        CanFocus = true,
                        TabDepth = 0
                    };

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
                            Text = $"=== {agentId} output ===\n"
                        };
                        agentView.SetScheme(new Scheme
                        {
                            Normal = new TgAttr(Color.White, Color.Black),
                            Focus = new TgAttr(Color.BrightYellow, Color.Black)
                        });

                        _agentTabs.InsertTab(i, agentView);
                        _agentOutputViews[agentId] = (agentView, agentView);
                        _tabIndex = i + 1;
                    }

                    _agentOutputFrame.Add(_agentTabs);

                    commandLabel.Y = Pos.Bottom(_agentOutputFrame);
                    workLabel.Y = Pos.Bottom(_agentOutputFrame);
                    commandField.Y = Pos.Bottom(_agentOutputFrame) + 1;
                    sendButton.Y = Pos.Bottom(_agentOutputFrame) + 1;
                    cancelButton.Y = Pos.Bottom(_agentOutputFrame) + 2;

                    _top.Add(_orchestratorFrame);
                    _top.Add(_agentOutputFrame);

                    _flushTimer = new Timer(FlushOutputBuffers, null, FlushIntervalMs, FlushIntervalMs);

                    _top.SetNeedsDraw();
                }
                catch (Exception ex)
                {
                    SessionLogger.Instance.LogError($"Failed to enter orchestration mode: {ex.Message}");
                }
            });
        }

        private void FlushOutputBuffers(object? state)
        {
            if (!_orchestrationMode || _isExiting) return;

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

            if (orchestratorText.Length > 0 || agentTexts.Count > 0)
            {
                Application.Invoke(() =>
                {
                    if (!_orchestrationMode || _isExiting) return;

                    try
                    {
                        if (orchestratorText.Length > 0 && _orchestratorView?.SuperView != null)
                        {
                            var currentText = _orchestratorView.Text ?? "";
                            _orchestratorView.Text = currentText + orchestratorText;
                            _orchestratorView.MoveEnd();
                        }

                        lock (_lock)
                        {
                            foreach (var kvp in agentTexts)
                            {
                                if (_agentOutputViews.TryGetValue(kvp.Key, out var tabInfo))
                                {
                                    if (tabInfo.textView.SuperView != null)
                                    {
                                        var currentText = tabInfo.textView.Text ?? "";
                                        tabInfo.textView.Text = currentText + kvp.Value;
                                        tabInfo.textView.MoveEnd();
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

        public void Exit(View actionView, Label commandLabel, Label workLabel, TextView commandField, Button sendButton, Button cancelButton)
        {
            if (!_orchestrationMode) return;

            SessionLogger.Instance.LogInfo("Exiting orchestration mode...");
            _isExiting = true;
            _flushTimer?.Dispose();
            _flushTimer = null;
            _agentOutputBuffers.Clear();
            lock (_orchestratorBuffer) { _orchestratorBuffer.Clear(); }

            Thread.Sleep(100);
            _orchestrationMode = false;

            Application.Invoke(() =>
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
                    _agentTabs = null;

                    actionView.Visible = true;

                    commandLabel.Y = Pos.Bottom(actionView);
                    workLabel.Y = Pos.Bottom(actionView);
                    commandField.Y = Pos.Bottom(actionView) + 1;
                    commandField.Height = 4;
                    sendButton.Y = Pos.Bottom(actionView) + 1;
                    cancelButton.Y = Pos.Bottom(actionView) + 2;

                    _top.SetNeedsLayout();
                    _top.SetNeedsDraw();

                    commandField.SetFocus();

                    _isExiting = false;
                    SessionLogger.Instance.LogInfo("Exited orchestration mode successfully");
                }
                catch (Exception ex)
                {
                    SessionLogger.Instance.LogError($"Failed to exit orchestration mode: {ex.Message}");
                    _isExiting = false;
                }
            });
        }

        public TextView? GetOrCreateAgentView(string agentId)
        {
            lock (_lock)
            {
                if (_agentOutputViews.TryGetValue(agentId, out var existing))
                    return existing.textView;

                if (_agentTabs == null) return null;

                var agentView = new TextView
                {
                    X = 0,
                    Y = 0,
                    Width = Dim.Fill(),
                    Height = Dim.Fill(),
                    ReadOnly = true,
                    WordWrap = true,
                    Text = $"=== {agentId} output ===\n"
                };
                agentView.SetScheme(new Scheme
                {
                    Normal = new TgAttr(Color.White, Color.Black),
                    Focus = new TgAttr(Color.BrightYellow, Color.Black)
                });

                Application.Invoke(() =>
                {
                    _agentTabs.InsertTab(_tabIndex, agentView);
                    _tabIndex++;
                });

                _agentOutputViews[agentId] = (agentView, agentView);
                return agentView;
            }
        }

        public void AppendAgentOutput(string agentId, string text)
        {
            if (!_orchestrationMode || _isExiting) return;
            if (text == "\r") return;
            text = text.Replace("\r", "");

            GetOrCreateAgentView(agentId);

            var buffer = _agentOutputBuffers.GetOrAdd(agentId, _ => new StringBuilder());
            lock (buffer)
            {
                buffer.Append(text);
            }
        }

        public void AppendOrchestratorStatus(string text, TextView? fallbackView = null)
        {
            if (_isExiting && fallbackView == null) return;

            if (fallbackView != null && (_isExiting || !_orchestrationMode))
            {
                Application.Invoke(() =>
                {
                    try
                    {
                        if (fallbackView.SuperView != null)
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
