using System.Text.Json;
using thuvu.Models;
using thuvu.Services;

namespace thuvu.Tools
{
    /// <summary>
    /// Implementation of the delegate_to_agent tool for sub-agent delegation.
    /// </summary>
    public static class DelegateToAgentToolImpl
    {
        private static SubAgentExecutor? _executor;
        private static string _currentSessionId = "";
        private static long? _currentParentMessageId;
        private static int _currentDepth = 0;
        private static List<ChatMessage> _currentContext = new();

        /// <summary>
        /// Initialize the delegation context for the current session.
        /// Call this before processing messages that might use delegation.
        /// </summary>
        public static void SetContext(
            HttpClient httpClient,
            string sessionId,
            long? parentMessageId,
            int currentDepth,
            List<ChatMessage> context)
        {
            _executor = new SubAgentExecutor(httpClient);
            _currentSessionId = sessionId;
            _currentParentMessageId = parentMessageId;
            _currentDepth = currentDepth;
            _currentContext = context;
        }

        /// <summary>
        /// Execute the delegate_to_agent tool.
        /// </summary>
        public static async Task<string> ExecuteAsync(string argsJson, CancellationToken ct = default)
        {
            try
            {
                // Check if delegation is enabled
                var rolesConfig = AgentRolesRegistry.Instance;
                if (!rolesConfig.Enabled)
                {
                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = "Sub-agent delegation is not enabled. Set AgentRoles.Enabled = true in appsettings.json."
                    });
                }

                // Parse arguments
                using var doc = JsonDocument.Parse(argsJson);
                var root = doc.RootElement;

                var role = root.TryGetProperty("role", out var r) ? r.GetString() ?? "" : "";
                var task = root.TryGetProperty("task", out var t) ? t.GetString() ?? "" : "";
                var contextFiles = root.TryGetProperty("context_files", out var cf)
                    ? cf.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList()
                    : null;
                var successCriteria = root.TryGetProperty("success_criteria", out var sc)
                    ? sc.GetString() : null;

                // Validate required fields
                if (string.IsNullOrEmpty(role))
                {
                    return JsonSerializer.Serialize(new { success = false, error = "Missing required parameter: role" });
                }
                if (string.IsNullOrEmpty(task))
                {
                    return JsonSerializer.Serialize(new { success = false, error = "Missing required parameter: task" });
                }

                // Validate role exists
                var roleDefinition = rolesConfig.GetRole(role);
                if (roleDefinition == null)
                {
                    var validRoles = string.Join(", ", rolesConfig.Roles.Select(r => r.RoleId));
                    return JsonSerializer.Serialize(new 
                    { 
                        success = false, 
                        error = $"Unknown role: {role}. Valid roles: {validRoles}" 
                    });
                }

                // Check if executor is initialized
                if (_executor == null)
                {
                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = "Sub-agent executor not initialized. This is an internal error."
                    });
                }

                // Build context
                var context = new SubAgentContext
                {
                    SessionId = _currentSessionId,
                    ParentMessageId = _currentParentMessageId,
                    Role = role,
                    Task = task,
                    ContextFiles = contextFiles,
                    SuccessCriteria = successCriteria,
                    CurrentDepth = _currentDepth,
                    ParentContext = _currentContext
                };

                // Execute sub-agent
                var result = await _executor.ExecuteAsync(context, ct);

                // Return result as JSON
                return JsonSerializer.Serialize(new
                {
                    success = result.Success,
                    status = result.Status,
                    summary = result.Summary,
                    details = result.Details,
                    files_modified = result.FilesModified,
                    files_created = result.FilesCreated,
                    suggestions = result.Suggestions,
                    error = result.ErrorMessage,
                    iteration_count = result.IterationCount,
                    duration_ms = result.DurationMs,
                    bailout_reason = result.BailoutReason,
                    role = role
                }, new JsonSerializerOptions { WriteIndented = false });
            }
            catch (Exception ex)
            {
                AgentLogger.LogError("delegate_to_agent failed: {Error}", ex.Message);
                return JsonSerializer.Serialize(new
                {
                    success = false,
                    error = ex.Message
                });
            }
        }
    }
}
