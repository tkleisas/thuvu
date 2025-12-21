using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Terminal.Gui;
using thuvu.Models;

namespace thuvu.Tui
{
    /// <summary>
    /// Message types for the TUI queue
    /// </summary>
    public enum TuiMessageType
    {
        MainOutput,       // Output to main action view
        OrchestratorStatus, // Status updates for orchestrator panel
        AgentOutput,      // Output to agent-specific tab
        ToolProgress,     // Tool execution progress
        WorkLabelUpdate   // Work label text update
    }
    
    /// <summary>
    /// A queued message for the TUI
    /// </summary>
    public class TuiMessage
    {
        public TuiMessageType Type { get; init; }
        public string Text { get; init; } = "";
        public string? AgentId { get; init; }
        public bool ScrollToEnd { get; init; } = true;
        public DateTime Timestamp { get; init; } = DateTime.Now;
    }
    
    /// <summary>
    /// Thread-safe message queue for TUI updates.
    /// All UI updates go through this queue to prevent race conditions and screen corruption.
    /// </summary>
    public class TuiMessageQueue : IDisposable
    {
        private readonly ConcurrentQueue<TuiMessage> _queue = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _processingTask;
        private readonly Action<TuiMessage> _processMessage;
        private volatile bool _isRunning = true;
        private readonly int _batchSize;
        private readonly int _processingIntervalMs;
        
        /// <summary>
        /// Creates a new message queue
        /// </summary>
        /// <param name="processMessage">Callback to process each message on UI thread</param>
        /// <param name="batchSize">Max messages to process per cycle (default 10)</param>
        /// <param name="processingIntervalMs">Interval between processing cycles (default 50ms)</param>
        public TuiMessageQueue(Action<TuiMessage> processMessage, int batchSize = 10, int processingIntervalMs = 50)
        {
            _processMessage = processMessage;
            _batchSize = batchSize;
            _processingIntervalMs = processingIntervalMs;
            _processingTask = Task.Run(ProcessQueueLoop);
        }
        
        /// <summary>
        /// Enqueue a message for display
        /// </summary>
        public void Enqueue(TuiMessage message)
        {
            if (!_isRunning) return;
            _queue.Enqueue(message);
        }
        
        /// <summary>
        /// Enqueue main output text
        /// </summary>
        public void EnqueueMainOutput(string text, bool scrollToEnd = true)
        {
            Enqueue(new TuiMessage 
            { 
                Type = TuiMessageType.MainOutput, 
                Text = text,
                ScrollToEnd = scrollToEnd
            });
        }
        
        /// <summary>
        /// Enqueue orchestrator status text
        /// </summary>
        public void EnqueueOrchestratorStatus(string text)
        {
            Enqueue(new TuiMessage 
            { 
                Type = TuiMessageType.OrchestratorStatus, 
                Text = text 
            });
        }
        
        /// <summary>
        /// Enqueue agent output text
        /// </summary>
        public void EnqueueAgentOutput(string agentId, string text)
        {
            Enqueue(new TuiMessage 
            { 
                Type = TuiMessageType.AgentOutput, 
                Text = text,
                AgentId = agentId
            });
        }
        
        /// <summary>
        /// Enqueue tool progress update
        /// </summary>
        public void EnqueueToolProgress(string text)
        {
            Enqueue(new TuiMessage 
            { 
                Type = TuiMessageType.ToolProgress, 
                Text = text 
            });
        }
        
        /// <summary>
        /// Enqueue work label update
        /// </summary>
        public void EnqueueWorkLabel(string text)
        {
            Enqueue(new TuiMessage 
            { 
                Type = TuiMessageType.WorkLabelUpdate, 
                Text = text 
            });
        }
        
        /// <summary>
        /// Background task that processes queued messages
        /// </summary>
        private async Task ProcessQueueLoop()
        {
            while (_isRunning && !_cts.Token.IsCancellationRequested)
            {
                try
                {
                    var processedCount = 0;
                    var messagesToProcess = new System.Collections.Generic.List<TuiMessage>();
                    
                    // Dequeue batch of messages
                    while (processedCount < _batchSize && _queue.TryDequeue(out var message))
                    {
                        messagesToProcess.Add(message);
                        processedCount++;
                    }
                    
                    if (messagesToProcess.Count > 0)
                    {
                        // Process all messages on UI thread in one batch
                        Application.Invoke(() =>
                        {
                            foreach (var msg in messagesToProcess)
                            {
                                try
                                {
                                    _processMessage(msg);
                                }
                                catch (Exception ex)
                                {
                                    SessionLogger.Instance.LogError($"Error processing TUI message: {ex.Message}");
                                }
                            }
                        });
                    }
                    
                    await Task.Delay(_processingIntervalMs, _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    SessionLogger.Instance.LogError($"TUI message queue error: {ex.Message}");
                    await Task.Delay(100, _cts.Token);
                }
            }
        }
        
        /// <summary>
        /// Flush remaining messages (blocking)
        /// </summary>
        public void Flush()
        {
            var timeout = DateTime.Now.AddSeconds(5);
            while (!_queue.IsEmpty && DateTime.Now < timeout)
            {
                Thread.Sleep(50);
            }
        }
        
        public void Dispose()
        {
            _isRunning = false;
            _cts.Cancel();
            
            try
            {
                _processingTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch { }
            
            _cts.Dispose();
        }
    }
}
