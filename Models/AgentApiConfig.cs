using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace thuvu.Models
{
    /// <summary>
    /// Configuration for the Agent Communication API.
    /// Enables agents to communicate with each other via HTTP.
    /// </summary>
    public sealed class AgentApiConfig
    {
        /// <summary>
        /// Enable/disable the Agent API server.
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Port to listen on for API requests.
        /// </summary>
        public int Port { get; set; } = 5001;

        /// <summary>
        /// Unique name for this agent.
        /// </summary>
        public string AgentName { get; set; } = "Agent";

        /// <summary>
        /// Description of this agent's purpose/capabilities.
        /// </summary>
        public string AgentDescription { get; set; } = "";

        /// <summary>
        /// Use HTTPS instead of HTTP.
        /// </summary>
        public bool UseHttps { get; set; } = false;

        /// <summary>
        /// Bearer token for authenticating incoming requests.
        /// If empty, no authentication required.
        /// </summary>
        public string BearerToken { get; set; } = "";

        /// <summary>
        /// List of known agents this agent can communicate with.
        /// </summary>
        public List<KnownAgent> KnownAgents { get; set; } = new();

        /// <summary>
        /// Maximum number of completed jobs to keep in history.
        /// </summary>
        public int MaxJobHistory { get; set; } = 50;

        /// <summary>
        /// Running in headless mode (no interactive console).
        /// </summary>
        public bool Headless { get; set; } = false;

        public static AgentApiConfig Instance { get; private set; } = new();

        /// <summary>
        /// Get the base URL for this agent's API.
        /// </summary>
        public string GetBaseUrl()
        {
            var scheme = UseHttps ? "https" : "http";
            return $"{scheme}://localhost:{Port}";
        }

        public static void LoadConfig(string? configPath = null)
        {
            var path = configPath ?? GetConfigPath();
            try
            {
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("AgentApiConfig", out var section))
                    {
                        Instance = JsonSerializer.Deserialize<AgentApiConfig>(section.GetRawText(), options) ?? new AgentApiConfig();
                        AgentLogger.LogInfo("Agent API config loaded: Name={Name}, Port={Port}, Enabled={Enabled}",
                            Instance.AgentName, Instance.Port, Instance.Enabled);
                    }
                    else
                    {
                        Instance = new AgentApiConfig();
                        AgentLogger.LogInfo("Agent API config using defaults (disabled)");
                    }
                }
                else
                {
                    Instance = new AgentApiConfig();
                }
            }
            catch (Exception ex)
            {
                AgentLogger.LogError("Failed to load AgentApiConfig: {Error}", ex.Message);
                Instance = new AgentApiConfig();
            }
        }

        public static string GetConfigPath()
        {
            var localPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            var env = Environment.GetEnvironmentVariable("LM_AGENT_CONFIG");
            if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env);
            return localPath;
        }

        /// <summary>
        /// Apply command-line overrides to the config.
        /// </summary>
        public void ApplyOverrides(int? port = null, string? agentName = null, bool? headless = null, bool? enabled = null)
        {
            if (port.HasValue) Port = port.Value;
            if (!string.IsNullOrEmpty(agentName)) AgentName = agentName;
            if (headless.HasValue) Headless = headless.Value;
            if (enabled.HasValue) Enabled = enabled.Value;

            // Allow token override via environment variable (more secure than CLI arg)
            var envToken = Environment.GetEnvironmentVariable("THUVU_API_TOKEN");
            if (!string.IsNullOrEmpty(envToken))
                BearerToken = envToken;
        }
    }

    /// <summary>
    /// Configuration for a known remote agent.
    /// </summary>
    public class KnownAgent
    {
        /// <summary>
        /// Friendly name for the agent.
        /// </summary>
        public string Name { get; set; } = "";

        /// <summary>
        /// Base URL of the agent's API (e.g., "http://localhost:5002").
        /// </summary>
        public string Url { get; set; } = "";

        /// <summary>
        /// Bearer token for authenticating with this agent.
        /// </summary>
        public string Token { get; set; } = "";

        /// <summary>
        /// Optional description of what this agent does.
        /// </summary>
        public string Description { get; set; } = "";
    }
}
