using System;
using System.Collections.Generic;

namespace thuvu.Models
{
    /// <summary>
    /// Result of MCP code execution
    /// </summary>
    public class McpExecutionResult
    {
        /// <summary>
        /// Whether execution completed successfully
        /// </summary>
        public bool Success { get; init; }

        /// <summary>
        /// The result returned by the executed code
        /// </summary>
        public string? Result { get; init; }

        /// <summary>
        /// Error message if execution failed
        /// </summary>
        public string? Error { get; init; }

        /// <summary>
        /// List of tool calls made during execution
        /// </summary>
        public List<ToolCallLog> ToolCalls { get; init; } = new();

        /// <summary>
        /// Total execution duration
        /// </summary>
        public TimeSpan Duration { get; init; }
    }

    /// <summary>
    /// Log entry for a tool call made during MCP execution
    /// </summary>
    public class ToolCallLog
    {
        /// <summary>
        /// Name of the tool that was called
        /// </summary>
        public string ToolName { get; init; } = string.Empty;

        /// <summary>
        /// Arguments passed to the tool (JSON)
        /// </summary>
        public string Arguments { get; init; } = "{}";

        /// <summary>
        /// Result returned by the tool (JSON)
        /// </summary>
        public string Result { get; init; } = string.Empty;

        /// <summary>
        /// Duration of the tool call
        /// </summary>
        public TimeSpan Duration { get; init; }

        /// <summary>
        /// Whether the tool call succeeded
        /// </summary>
        public bool Success { get; init; }
    }
}
