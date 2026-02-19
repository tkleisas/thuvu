using Microsoft.Extensions.Logging;
using System;

namespace thuvu.Models
{
    /// <summary>
    /// Centralized logging service for the THUVU agent.
    /// Provides structured logging with configurable output.
    /// </summary>
    public static class AgentLogger
    {
        private static ILoggerFactory? _loggerFactory;
        private static ILogger? _defaultLogger;

        public static void Initialize(LogLevel minLevel = LogLevel.Information)
        {
            _loggerFactory = LoggerFactory.Create(builder =>
            {
                builder
                    .SetMinimumLevel(minLevel)
                    .AddConsole(options =>
                    {
                        options.FormatterName = "simple";
                    });
            });
            _defaultLogger = _loggerFactory.CreateLogger("THUVU");
        }

        public static ILogger GetLogger(string categoryName)
        {
            if (_loggerFactory == null)
                Initialize();
            return _loggerFactory!.CreateLogger(categoryName);
        }

        public static ILogger<T> GetLogger<T>()
        {
            if (_loggerFactory == null)
                Initialize();
            return _loggerFactory!.CreateLogger<T>();
        }

        // Convenience methods for default logger
        public static void LogInfo(string message, params object[] args)
        {
            if (_defaultLogger == null) Initialize();
            _defaultLogger!.LogInformation(message, args);
        }

        public static void LogWarning(string message, params object[] args)
        {
            if (_defaultLogger == null) Initialize();
            _defaultLogger!.LogWarning(message, args);
        }

        public static void LogError(string message, params object[] args)
        {
            if (_defaultLogger == null) Initialize();
            _defaultLogger!.LogError(message, args);
        }

        public static void LogError(Exception ex, string message, params object[] args)
        {
            if (_defaultLogger == null) Initialize();
            _defaultLogger!.LogError(ex, message, args);
        }

        public static void LogDebug(string message, params object[] args)
        {
            if (_defaultLogger == null) Initialize();
            _defaultLogger!.LogDebug(message, args);
        }

        public static void LogToolCall(string toolName, string args, string result)
        {
            if (_defaultLogger == null) Initialize();
            _defaultLogger!.LogInformation("[TOOL] {ToolName}({Args}) => {ResultLength} chars", 
                toolName, args, result?.Length ?? 0);
        }

        public static void LogLlmRequest(string model, int messageCount)
        {
            if (_defaultLogger == null) Initialize();
            _defaultLogger!.LogInformation("[LLM] Request to {Model} with {MessageCount} messages", 
                model, messageCount);
        }

        public static void LogLlmResponse(int promptTokens, int completionTokens)
        {
            if (_defaultLogger == null) Initialize();
            _defaultLogger!.LogInformation("[LLM] Response: prompt={PromptTokens}, completion={CompletionTokens}", 
                promptTokens, completionTokens);
        }

        public static void Shutdown()
        {
            _loggerFactory?.Dispose();
            _loggerFactory = null;
            _defaultLogger = null;
        }
    }
}
