using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace thuvu.Tools.ProcessManagement
{
    /// <summary>
    /// Tool implementations for process management
    /// </summary>
    public static class ProcessToolImpl
    {
        /// <summary>
        /// Start a new background process
        /// </summary>
        public static Task<string> ProcessStartAsync(string rawArgs)
        {
            try
            {
                using var doc = JsonDocument.Parse(rawArgs);
                var root = doc.RootElement;

                var cmd = root.TryGetProperty("cmd", out var cmdEl) && cmdEl.ValueKind == JsonValueKind.String
                    ? cmdEl.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(cmd))
                {
                    return Task.FromResult(JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = "cmd is required"
                    }));
                }

                // Check if command is allowed
                if (!RunProcessToolImpl.AllowedCmds.Contains(cmd))
                {
                    return Task.FromResult(JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = $"Command '{cmd}' is not in the allowed list"
                    }));
                }

                // Parse arguments
                var args = new List<string>();
                if (root.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var arg in argsEl.EnumerateArray())
                    {
                        if (arg.ValueKind == JsonValueKind.String)
                            args.Add(arg.GetString() ?? "");
                    }
                }

                // Get working directory
                var workDir = thuvu.Models.AgentContext.GetEffectiveWorkDirectory();
                var cwd = root.TryGetProperty("cwd", out var cwdEl) && cwdEl.ValueKind == JsonValueKind.String
                    ? cwdEl.GetString()
                    : null;

                var effectiveCwd = string.IsNullOrWhiteSpace(cwd) ? workDir : cwd;

                // Start the process
                var session = ProcessSessionManager.Instance.StartProcess(cmd, args.ToArray(), effectiveCwd);

                return Task.FromResult(JsonSerializer.Serialize(new
                {
                    success = true,
                    session_id = session.SessionId,
                    pid = session.ProcessId,
                    command = cmd,
                    arguments = args,
                    working_directory = effectiveCwd,
                    started_at = session.StartedAt.ToString("O")
                }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(JsonSerializer.Serialize(new
                {
                    success = false,
                    error = ex.Message
                }));
            }
        }

        /// <summary>
        /// Read output from a background process
        /// </summary>
        public static async Task<string> ProcessReadAsync(string rawArgs)
        {
            try
            {
                using var doc = JsonDocument.Parse(rawArgs);
                var root = doc.RootElement;

                var sessionId = root.TryGetProperty("session_id", out var sidEl) && sidEl.ValueKind == JsonValueKind.String
                    ? sidEl.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(sessionId))
                {
                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = "session_id is required"
                    });
                }

                var session = ProcessSessionManager.Instance.GetSession(sessionId);
                if (session == null)
                {
                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = $"Session '{sessionId}' not found"
                    });
                }

                // Optional: read all output instead of just new output
                var readAll = root.TryGetProperty("all", out var allEl) && allEl.ValueKind == JsonValueKind.True;

                // Optional: wait for output (useful when expecting output)
                var waitMs = root.TryGetProperty("wait_ms", out var waitEl) && waitEl.ValueKind == JsonValueKind.Number
                    ? Math.Clamp(waitEl.GetInt32(), 0, 30000)
                    : 0;

                if (waitMs > 0)
                {
                    await Task.Delay(waitMs);
                }

                var (stdout, stderr) = readAll ? session.ReadAllOutput() : session.ReadOutput();

                return JsonSerializer.Serialize(new
                {
                    success = true,
                    session_id = sessionId,
                    is_running = session.IsRunning,
                    exit_code = session.ExitCode,
                    stdout,
                    stderr
                });
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = ex.Message
                });
            }
        }

        /// <summary>
        /// Write input to a background process
        /// </summary>
        public static Task<string> ProcessWriteAsync(string rawArgs)
        {
            try
            {
                using var doc = JsonDocument.Parse(rawArgs);
                var root = doc.RootElement;

                var sessionId = root.TryGetProperty("session_id", out var sidEl) && sidEl.ValueKind == JsonValueKind.String
                    ? sidEl.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(sessionId))
                {
                    return Task.FromResult(JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = "session_id is required"
                    }));
                }

                var session = ProcessSessionManager.Instance.GetSession(sessionId);
                if (session == null)
                {
                    return Task.FromResult(JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = $"Session '{sessionId}' not found"
                    }));
                }

                var input = root.TryGetProperty("input", out var inputEl) && inputEl.ValueKind == JsonValueKind.String
                    ? inputEl.GetString() ?? ""
                    : "";

                var addNewline = !root.TryGetProperty("no_newline", out var noNlEl) || noNlEl.ValueKind != JsonValueKind.True;

                if (addNewline)
                    session.WriteLineInput(input);
                else
                    session.WriteInput(input);

                return Task.FromResult(JsonSerializer.Serialize(new
                {
                    success = true,
                    session_id = sessionId,
                    bytes_written = input.Length + (addNewline ? Environment.NewLine.Length : 0)
                }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(JsonSerializer.Serialize(new
                {
                    success = false,
                    error = ex.Message
                }));
            }
        }

        /// <summary>
        /// Get status of a background process or list all sessions
        /// </summary>
        public static Task<string> ProcessStatusAsync(string rawArgs)
        {
            try
            {
                using var doc = JsonDocument.Parse(rawArgs);
                var root = doc.RootElement;

                var sessionId = root.TryGetProperty("session_id", out var sidEl) && sidEl.ValueKind == JsonValueKind.String
                    ? sidEl.GetString()
                    : null;

                // If no session_id, list all sessions
                if (string.IsNullOrWhiteSpace(sessionId))
                {
                    var sessions = ProcessSessionManager.Instance.ListSessions();
                    return Task.FromResult(JsonSerializer.Serialize(new
                    {
                        success = true,
                        session_count = sessions.Length,
                        sessions = sessions.Select(s => new
                        {
                            session_id = s.SessionId,
                            pid = s.ProcessId,
                            command = s.Command,
                            is_running = s.IsRunning,
                            exit_code = s.ExitCode,
                            started_at = s.StartedAt.ToString("O"),
                            runtime_seconds = (DateTime.UtcNow - s.StartedAt).TotalSeconds
                        }).ToArray()
                    }));
                }

                var session = ProcessSessionManager.Instance.GetSession(sessionId);
                if (session == null)
                {
                    return Task.FromResult(JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = $"Session '{sessionId}' not found"
                    }));
                }

                return Task.FromResult(JsonSerializer.Serialize(new
                {
                    success = true,
                    session_id = session.SessionId,
                    pid = session.ProcessId,
                    command = session.Command,
                    arguments = session.Arguments,
                    working_directory = session.WorkingDirectory,
                    is_running = session.IsRunning,
                    exit_code = session.ExitCode,
                    started_at = session.StartedAt.ToString("O"),
                    runtime_seconds = (DateTime.UtcNow - session.StartedAt).TotalSeconds
                }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(JsonSerializer.Serialize(new
                {
                    success = false,
                    error = ex.Message
                }));
            }
        }

        /// <summary>
        /// Stop a background process
        /// </summary>
        public static Task<string> ProcessStopAsync(string rawArgs)
        {
            try
            {
                using var doc = JsonDocument.Parse(rawArgs);
                var root = doc.RootElement;

                var sessionId = root.TryGetProperty("session_id", out var sidEl) && sidEl.ValueKind == JsonValueKind.String
                    ? sidEl.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(sessionId))
                {
                    return Task.FromResult(JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = "session_id is required"
                    }));
                }

                // Get final output before stopping
                var session = ProcessSessionManager.Instance.GetSession(sessionId);
                string? finalStdout = null;
                string? finalStderr = null;
                int? exitCode = null;
                
                if (session != null)
                {
                    var (stdout, stderr) = session.ReadAllOutput();
                    finalStdout = stdout;
                    finalStderr = stderr;
                    exitCode = session.ExitCode;
                }

                var force = root.TryGetProperty("force", out var forceEl) && forceEl.ValueKind == JsonValueKind.True;
                var stopped = ProcessSessionManager.Instance.StopSession(sessionId, force);

                return Task.FromResult(JsonSerializer.Serialize(new
                {
                    success = stopped,
                    session_id = sessionId,
                    exit_code = exitCode,
                    final_stdout = finalStdout,
                    final_stderr = finalStderr,
                    message = stopped ? "Process stopped and session removed" : "Session not found"
                }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(JsonSerializer.Serialize(new
                {
                    success = false,
                    error = ex.Message
                }));
            }
        }
    }
}
