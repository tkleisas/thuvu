using System.Text.Json;

namespace thuvu.Tools
{
    /// <summary>
    /// Fire-and-forget tool that spawns a new agent in a separate chat tab.
    /// The host (Desktop) must register a handler via <see cref="Handler"/>.
    /// </summary>
    public static class CreateAgentToolImpl
    {
        /// <summary>
        /// Callback that the host registers to actually create the agent.
        /// Returns (agentId, workDirectory) on success.
        /// </summary>
        public static Func<CreateAgentRequest, Task<CreateAgentResult>>? Handler { get; set; }

        public static async Task<string> ExecuteAsync(string argsJson, CancellationToken ct)
        {
            if (Handler == null)
                return JsonSerializer.Serialize(new { error = "create_agent is not available in this host." });

            try
            {
                var args = JsonDocument.Parse(argsJson).RootElement;

                var prompt = args.TryGetProperty("prompt", out var p) ? p.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(prompt))
                    return JsonSerializer.Serialize(new { error = "prompt is required" });

                var request = new CreateAgentRequest
                {
                    Prompt = prompt,
                    Name = args.TryGetProperty("name", out var n) ? n.GetString() : null,
                    Model = args.TryGetProperty("model", out var m) ? m.GetString() : null,
                    PromptTemplate = args.TryGetProperty("prompt_template", out var pt) ? pt.GetString() : null
                };

                var result = await Handler(request).ConfigureAwait(false);

                return JsonSerializer.Serialize(new
                {
                    agent_id = result.AgentId,
                    work_directory = result.WorkDirectory,
                    status = "started",
                    message = $"Agent '{result.AgentId}' created and working on the task. " +
                              "It runs independently in its own chat tab. " +
                              "Use read_file to check any progress files it writes."
                });
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { error = ex.Message });
            }
        }
    }

    public class CreateAgentRequest
    {
        public string Prompt { get; set; } = "";
        public string? Name { get; set; }
        public string? Model { get; set; }
        public string? PromptTemplate { get; set; }
    }

    public class CreateAgentResult
    {
        public string AgentId { get; set; } = "";
        public string? WorkDirectory { get; set; }
    }
}
