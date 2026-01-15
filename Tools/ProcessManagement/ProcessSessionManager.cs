using System;
using System.Collections.Concurrent;
using System.Linq;

namespace thuvu.Tools.ProcessManagement
{
    /// <summary>
    /// Singleton manager for background process sessions
    /// </summary>
    public class ProcessSessionManager : IDisposable
    {
        private static readonly Lazy<ProcessSessionManager> _instance = new(() => new ProcessSessionManager());
        public static ProcessSessionManager Instance => _instance.Value;

        private readonly ConcurrentDictionary<string, ProcessSession> _sessions = new();
        private int _sessionCounter = 0;
        private bool _disposed;

        private ProcessSessionManager() { }

        /// <summary>
        /// Create and start a new process session
        /// </summary>
        public ProcessSession StartProcess(string command, string[] arguments, string workingDirectory)
        {
            var sessionId = GenerateSessionId();
            var session = new ProcessSession(sessionId, command, arguments, workingDirectory);
            
            session.Start();
            
            if (!_sessions.TryAdd(sessionId, session))
            {
                session.Dispose();
                throw new InvalidOperationException("Failed to register session");
            }

            return session;
        }

        /// <summary>
        /// Get a session by ID
        /// </summary>
        public ProcessSession? GetSession(string sessionId)
        {
            return _sessions.TryGetValue(sessionId, out var session) ? session : null;
        }

        /// <summary>
        /// List all active sessions
        /// </summary>
        public ProcessSession[] ListSessions()
        {
            return _sessions.Values.ToArray();
        }

        /// <summary>
        /// Stop and remove a session
        /// </summary>
        public bool StopSession(string sessionId, bool force = false)
        {
            if (_sessions.TryRemove(sessionId, out var session))
            {
                session.Stop(force);
                session.Dispose();
                return true;
            }
            return false;
        }

        /// <summary>
        /// Clean up sessions that have exited
        /// </summary>
        public int CleanupExitedSessions()
        {
            var exitedSessions = _sessions
                .Where(kvp => !kvp.Value.IsRunning)
                .Select(kvp => kvp.Key)
                .ToList();

            int cleaned = 0;
            foreach (var sessionId in exitedSessions)
            {
                if (_sessions.TryRemove(sessionId, out var session))
                {
                    session.Dispose();
                    cleaned++;
                }
            }
            return cleaned;
        }

        /// <summary>
        /// Stop all sessions
        /// </summary>
        public void StopAllSessions()
        {
            foreach (var sessionId in _sessions.Keys.ToList())
            {
                StopSession(sessionId, force: true);
            }
        }

        private string GenerateSessionId()
        {
            var counter = System.Threading.Interlocked.Increment(ref _sessionCounter);
            return $"proc_{counter:D4}_{DateTime.UtcNow:HHmmss}";
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            StopAllSessions();
        }
    }
}
