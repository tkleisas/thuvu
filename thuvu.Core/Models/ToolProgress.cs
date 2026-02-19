using System;

namespace thuvu.Models
{
    /// <summary>
    /// Represents progress information for a tool execution
    /// </summary>
    public class ToolProgress
    {
        /// <summary>Tool name being executed</summary>
        public string ToolName { get; set; } = string.Empty;
        
        /// <summary>Tool arguments (JSON)</summary>
        public string ArgsJson { get; set; } = string.Empty;
        
        /// <summary>Current status of the tool execution</summary>
        public ToolStatus Status { get; set; } = ToolStatus.Pending;
        
        /// <summary>Time when the tool started executing</summary>
        public DateTime StartTime { get; set; }
        
        /// <summary>Time elapsed since tool started</summary>
        public TimeSpan Elapsed => DateTime.Now - StartTime;
        
        /// <summary>Elapsed time formatted as string (e.g., "5.2s" or "1m 30s")</summary>
        public string ElapsedFormatted
        {
            get
            {
                var elapsed = Elapsed;
                if (elapsed.TotalMinutes >= 1)
                    return $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s";
                return $"{elapsed.TotalSeconds:F1}s";
            }
        }
        
        /// <summary>Timeout for this tool execution</summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);
        
        /// <summary>Progress message (optional)</summary>
        public string? Message { get; set; }
        
        /// <summary>Result of the tool execution (set when completed)</summary>
        public string? Result { get; set; }
        
        /// <summary>Whether the tool execution timed out</summary>
        public bool TimedOut { get; set; }
        
        /// <summary>Whether the tool execution was cancelled</summary>
        public bool Cancelled { get; set; }
    }
    
    /// <summary>
    /// Status of a tool execution
    /// </summary>
    public enum ToolStatus
    {
        /// <summary>Tool is waiting to be executed</summary>
        Pending,
        
        /// <summary>Tool is currently running</summary>
        Running,
        
        /// <summary>Tool completed successfully</summary>
        Completed,
        
        /// <summary>Tool failed with an error</summary>
        Failed,
        
        /// <summary>Tool execution timed out</summary>
        TimedOut,
        
        /// <summary>Tool execution was cancelled</summary>
        Cancelled
    }
    
    /// <summary>
    /// Callback delegate for tool progress updates
    /// </summary>
    public delegate void ToolProgressCallback(ToolProgress progress);
}
