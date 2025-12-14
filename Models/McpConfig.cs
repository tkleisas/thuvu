using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace thuvu.Models
{
    /// <summary>
    /// Configuration for MCP (Model Context Protocol) code execution
    /// </summary>
    public class McpConfig
    {
        private static McpConfig? _instance;
        private static readonly object _lock = new();

        /// <summary>
        /// Whether MCP code execution is enabled
        /// </summary>
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Path to the Deno executable
        /// </summary>
        [JsonPropertyName("denoPath")]
        public string DenoPath { get; set; } = "deno";

        /// <summary>
        /// Default execution timeout in milliseconds
        /// </summary>
        [JsonPropertyName("defaultTimeout")]
        public int DefaultTimeout { get; set; } = 300000; // 5 minutes

        /// <summary>
        /// Maximum memory in MB for sandbox
        /// </summary>
        [JsonPropertyName("maxMemoryMb")]
        public int MaxMemoryMb { get; set; } = 512;

        /// <summary>
        /// Permission level for sandbox
        /// </summary>
        [JsonPropertyName("permissionLevel")]
        public string PermissionLevel { get; set; } = "readwrite";

        /// <summary>
        /// Directory for saved skills
        /// </summary>
        [JsonPropertyName("skillsDirectory")]
        public string SkillsDirectory { get; set; } = "./skills";

        /// <summary>
        /// Whether to enable audit logging
        /// </summary>
        [JsonPropertyName("auditLog")]
        public bool AuditLog { get; set; } = true;

        /// <summary>
        /// Whether to require user approval for code execution
        /// </summary>
        [JsonPropertyName("requireApproval")]
        public bool RequireApproval { get; set; } = true;

        /// <summary>
        /// Whether MCP mode is currently active for the session (not persisted)
        /// </summary>
        [JsonIgnore]
        public bool McpModeActive { get; set; } = false;

        /// <summary>
        /// Singleton instance
        /// </summary>
        public static McpConfig Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        _instance ??= new McpConfig();
                    }
                }
                return _instance;
            }
        }

        /// <summary>
        /// Get the config file path
        /// </summary>
        public static string GetConfigPath()
        {
            //read from config file in executable dir first
            var localPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            // Allow override via env var
            var env = Environment.GetEnvironmentVariable("LM_AGENT_CONFIG");
            if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env);
            return localPath;
        }

        /// <summary>
        /// Load configuration from file
        /// </summary>
        public static void LoadConfig()
        {
            var path = GetConfigPath();
            try
            {
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

                    // If your config is stored under a named section in the JSON file (for example "McpConfig"),
                    // extract that section and pass its raw JSON to the deserializer. Otherwise fall back to deserializing the whole file.
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    // Try multiple section names: "McpConfig", "Mcp"
                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("McpConfig", out var section))
                    {
                        var config = JsonSerializer.Deserialize<McpConfig>(section.GetRawText(), options);
                        if (config != null)
                        {
                            lock (_lock)
                            {
                                _instance = config;
                            }
                        }
                    }
                    else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Mcp", out var mcpSection))
                    {
                        var config = JsonSerializer.Deserialize<McpConfig>(mcpSection.GetRawText(), options);
                        if (config != null)
                        {
                            lock (_lock)
                            {
                                _instance = config;
                            }
                        }
                    }
                    else
                    {
                        var config = JsonSerializer.Deserialize<McpConfig>(json, options);
                        if (config != null)
                        {
                            lock (_lock)
                            {
                                _instance = config;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AgentLogger.LogWarning("Failed to load MCP config: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// Save configuration to file
        /// </summary>
        public static bool SaveConfig()
        {
            try
            {
                var path = GetConfigPath();
                var json = JsonSerializer.Serialize(Instance, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
                return true;
            }
            catch (Exception ex)
            {
                AgentLogger.LogError("Failed to save MCP config: {Message}", ex.Message);
                return false;
            }
        }
    }
}
