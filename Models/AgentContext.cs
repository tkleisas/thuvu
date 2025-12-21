using System;
using System.Threading;

namespace thuvu.Models
{
    /// <summary>
    /// Provides async-local context for agent-specific settings.
    /// This allows tools to be aware of which agent is calling them
    /// and use agent-specific settings like work directory.
    /// </summary>
    public static class AgentContext
    {
        private static readonly AsyncLocal<AgentContextData?> _current = new();
        
        /// <summary>
        /// Current agent context, or null if not in an agent scope
        /// </summary>
        public static AgentContextData? Current => _current.Value;
        
        /// <summary>
        /// Whether we're currently in an agent context
        /// </summary>
        public static bool IsInAgentContext => _current.Value != null;
        
        /// <summary>
        /// Get the effective work directory - agent-specific if in context, otherwise global
        /// </summary>
        public static string GetEffectiveWorkDirectory()
        {
            return _current.Value?.WorkDirectory ?? AgentConfig.GetWorkDirectory();
        }
        
        /// <summary>
        /// Get the effective token tracker - agent-specific if in context, otherwise global
        /// </summary>
        public static TokenTracker GetEffectiveTokenTracker()
        {
            return _current.Value?.TokenTracker ?? TokenTracker.Instance;
        }
        
        /// <summary>
        /// Run an action within an agent context
        /// </summary>
        public static async Task<T> RunInContextAsync<T>(AgentContextData context, Func<Task<T>> action)
        {
            var previous = _current.Value;
            try
            {
                _current.Value = context;
                return await action();
            }
            finally
            {
                _current.Value = previous;
            }
        }
        
        /// <summary>
        /// Run an action within an agent context (void return)
        /// </summary>
        public static async Task RunInContextAsync(AgentContextData context, Func<Task> action)
        {
            var previous = _current.Value;
            try
            {
                _current.Value = context;
                await action();
            }
            finally
            {
                _current.Value = previous;
            }
        }
        
        /// <summary>
        /// Create a new agent context for orchestrated execution
        /// </summary>
        public static AgentContextData CreateContext(string agentId, string workDirectory, int maxContextLength = 32768)
        {
            return new AgentContextData
            {
                AgentId = agentId,
                WorkDirectory = workDirectory,
                TokenTracker = new TokenTracker { MaxContextLength = maxContextLength },
                StartedAt = DateTime.Now
            };
        }
    }
    
    /// <summary>
    /// Data for agent-specific context
    /// </summary>
    public class AgentContextData
    {
        public string AgentId { get; set; } = "";
        public string WorkDirectory { get; set; } = "";
        public TokenTracker TokenTracker { get; set; } = new();
        public DateTime StartedAt { get; set; }
        public string? CurrentTaskId { get; set; }
        
        /// <summary>
        /// Log a message with agent context
        /// </summary>
        public void Log(string message)
        {
            SessionLogger.Instance.LogInfo($"[{AgentId}] {message}");
        }
    }
}
