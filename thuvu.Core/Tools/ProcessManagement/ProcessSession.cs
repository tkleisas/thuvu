using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace thuvu.Tools.ProcessManagement
{
    /// <summary>
    /// Represents a managed background process session
    /// </summary>
    public class ProcessSession : IDisposable
    {
        public string SessionId { get; }
        public int ProcessId { get; private set; }
        public string Command { get; }
        public string[] Arguments { get; }
        public string WorkingDirectory { get; }
        public DateTime StartedAt { get; }
        
        public bool IsRunning => _process != null && !_process.HasExited;
        public int? ExitCode => _process?.HasExited == true ? _process.ExitCode : null;
        
        private Process? _process;
        private readonly StringBuilder _stdout = new();
        private readonly StringBuilder _stderr = new();
        private readonly object _outputLock = new();
        private int _stdoutReadPosition = 0;
        private int _stderrReadPosition = 0;
        private bool _disposed;

        public ProcessSession(string sessionId, string command, string[] arguments, string workingDirectory)
        {
            SessionId = sessionId;
            Command = command;
            Arguments = arguments;
            WorkingDirectory = workingDirectory;
            StartedAt = DateTime.UtcNow;
        }

        /// <summary>
        /// Start the process
        /// </summary>
        public void Start()
        {
            if (_process != null)
                throw new InvalidOperationException("Process already started");

            var psi = new ProcessStartInfo(Command)
            {
                WorkingDirectory = WorkingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var arg in Arguments)
                psi.ArgumentList.Add(arg);

            _process = new Process { StartInfo = psi };
            
            _process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    lock (_outputLock)
                    {
                        _stdout.AppendLine(e.Data);
                    }
                }
            };

            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    lock (_outputLock)
                    {
                        _stderr.AppendLine(e.Data);
                    }
                }
            };

            _process.Start();
            ProcessId = _process.Id;
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        /// <summary>
        /// Read new output since last read
        /// </summary>
        public (string stdout, string stderr) ReadOutput()
        {
            lock (_outputLock)
            {
                var fullStdout = _stdout.ToString();
                var fullStderr = _stderr.ToString();
                
                var newStdout = fullStdout.Length > _stdoutReadPosition 
                    ? fullStdout.Substring(_stdoutReadPosition) 
                    : "";
                var newStderr = fullStderr.Length > _stderrReadPosition 
                    ? fullStderr.Substring(_stderrReadPosition) 
                    : "";
                
                _stdoutReadPosition = fullStdout.Length;
                _stderrReadPosition = fullStderr.Length;
                
                return (newStdout, newStderr);
            }
        }

        /// <summary>
        /// Read all output from the beginning
        /// </summary>
        public (string stdout, string stderr) ReadAllOutput()
        {
            lock (_outputLock)
            {
                return (_stdout.ToString(), _stderr.ToString());
            }
        }

        /// <summary>
        /// Write to process stdin
        /// </summary>
        public void WriteInput(string input)
        {
            if (_process == null || _process.HasExited)
                throw new InvalidOperationException("Process is not running");

            _process.StandardInput.Write(input);
            _process.StandardInput.Flush();
        }

        /// <summary>
        /// Write a line to process stdin
        /// </summary>
        public void WriteLineInput(string input)
        {
            if (_process == null || _process.HasExited)
                throw new InvalidOperationException("Process is not running");

            _process.StandardInput.WriteLine(input);
            _process.StandardInput.Flush();
        }

        /// <summary>
        /// Stop the process
        /// </summary>
        public void Stop(bool force = false)
        {
            if (_process == null || _process.HasExited)
                return;

            try
            {
                if (force)
                {
                    _process.Kill(entireProcessTree: true);
                }
                else
                {
                    // Try graceful shutdown first (close stdin)
                    try
                    {
                        _process.StandardInput.Close();
                    }
                    catch { }

                    // Wait a bit for graceful exit
                    if (!_process.WaitForExit(3000))
                    {
                        _process.Kill(entireProcessTree: true);
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // Process already exited
            }
        }

        /// <summary>
        /// Wait for the process to exit with timeout
        /// </summary>
        public bool WaitForExit(int timeoutMs)
        {
            if (_process == null)
                return true;

            return _process.WaitForExit(timeoutMs);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            
            try
            {
                Stop(force: true);
            }
            catch { }

            _process?.Dispose();
        }
    }
}
