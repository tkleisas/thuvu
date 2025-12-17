using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace thuvu.Models
{
    /// <summary>
    /// Session logger for detailed action/request/response logging
    /// </summary>
    public sealed class SessionLogger : IDisposable
    {
        private static SessionLogger? _instance;
        private readonly StreamWriter _writer;
        private readonly string _logPath;
        private readonly object _lock = new();
        private bool _disposed;

        public static SessionLogger Instance => _instance ??= new SessionLogger();
        
        public string LogPath => _logPath;

        private SessionLogger()
        {
            var logDir = Path.Combine(AgentConfig.GetWorkDirectory(), "logs");
            Directory.CreateDirectory(logDir);
            
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            _logPath = Path.Combine(logDir, $"session_{timestamp}.log");
            
            _writer = new StreamWriter(_logPath, append: false, Encoding.UTF8)
            {
                AutoFlush = true
            };
            
            LogHeader();
        }

        private void LogHeader()
        {
            _writer.WriteLine("═══════════════════════════════════════════════════════════════════════════════");
            _writer.WriteLine($"  THUVU Session Log - Started {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            _writer.WriteLine($"  Platform: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
            _writer.WriteLine($"  Working Directory: {AgentConfig.GetWorkDirectory()}");
            _writer.WriteLine($"  Model: {AgentConfig.Config.Model}");
            _writer.WriteLine("═══════════════════════════════════════════════════════════════════════════════");
            _writer.WriteLine();
        }

        public void LogUserInput(string input)
        {
            lock (_lock)
            {
                _writer.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ───────────────────────────────────────────────────────────");
                _writer.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 👤 USER INPUT:");
                _writer.WriteLine(input);
                _writer.WriteLine();
            }
        }

        public void LogLlmRequest(string model, int messageCount)
        {
            lock (_lock)
            {
                _writer.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 📤 LLM REQUEST: model={model}, messages={messageCount}");
            }
        }

        public void LogLlmResponse(string? content, int? toolCallCount, double elapsedSeconds)
        {
            lock (_lock)
            {
                _writer.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 📥 LLM RESPONSE: elapsed={elapsedSeconds:F2}s, toolCalls={toolCallCount ?? 0}");
                if (!string.IsNullOrEmpty(content))
                {
                    var preview = content.Length > 500 ? content.Substring(0, 500) + "..." : content;
                    _writer.WriteLine($"    Content: {preview}");
                }
                _writer.WriteLine();
            }
        }

        public void LogToolStart(string toolName, string argsJson)
        {
            lock (_lock)
            {
                var argsPreview = argsJson.Length > 200 ? argsJson.Substring(0, 200) + "..." : argsJson;
                _writer.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🔧 TOOL START: {toolName}");
                _writer.WriteLine($"    Args: {argsPreview}");
            }
        }

        public void LogToolEnd(string toolName, string result, double elapsedMs)
        {
            lock (_lock)
            {
                var resultPreview = result.Length > 500 ? result.Substring(0, 500) + "..." : result;
                _writer.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 🔧 TOOL END: {toolName} ({elapsedMs:F0}ms)");
                _writer.WriteLine($"    Result: {resultPreview}");
                _writer.WriteLine();
            }
        }

        public void LogToolError(string toolName, string error)
        {
            lock (_lock)
            {
                _writer.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ❌ TOOL ERROR: {toolName}");
                _writer.WriteLine($"    Error: {error}");
                _writer.WriteLine();
            }
        }

        public void LogToolProgress(string toolName, ToolStatus status, string elapsed, string? message = null)
        {
            lock (_lock)
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
                var msg = message != null ? $" - {message}" : "";
                _writer.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {statusIcon} TOOL PROGRESS: {toolName} [{status}] {elapsed}{msg}");
            }
        }

        public void LogToolTimeout(string toolName, double elapsedMs, double timeoutMs)
        {
            lock (_lock)
            {
                _writer.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ⏱ TOOL TIMEOUT: {toolName}");
                _writer.WriteLine($"    Elapsed: {elapsedMs:F0}ms, Timeout: {timeoutMs:F0}ms");
                _writer.WriteLine();
            }
        }

        public void LogError(string message, Exception? ex = null)
        {
            lock (_lock)
            {
                _writer.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ❌ ERROR: {message}");
                if (ex != null)
                {
                    _writer.WriteLine($"    Exception: {ex.GetType().Name}: {ex.Message}");
                    _writer.WriteLine($"    StackTrace: {ex.StackTrace}");
                }
                _writer.WriteLine();
            }
        }

        public void LogInfo(string message)
        {
            lock (_lock)
            {
                _writer.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] ℹ️ {message}");
            }
        }

        public void LogStreamingStats(int tokenCount, double elapsedSeconds, double tokensPerSecond)
        {
            lock (_lock)
            {
                _writer.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] 📊 STREAMING STATS: tokens={tokenCount}, elapsed={elapsedSeconds:F2}s, rate={tokensPerSecond:F1} t/s");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            
            lock (_lock)
            {
                _writer.WriteLine();
                _writer.WriteLine("═══════════════════════════════════════════════════════════════════════════════");
                _writer.WriteLine($"  Session ended {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                _writer.WriteLine("═══════════════════════════════════════════════════════════════════════════════");
                _writer.Dispose();
            }
            
            _instance = null;
        }
    }
}
