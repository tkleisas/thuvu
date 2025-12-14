using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace thuvu.Models
{
    // ----- Persistent config -----
    public sealed class AgentConfig
    {
        public string HostUrl { get; set; } = "http://127.0.0.1:1234";
        public string Model { get; set; } = "devstral-small-2-2512";
        public bool Stream { get; set; } = true;       // default: stream tokens
        public int TimeoutMs { get; set; } = 1_800_000;  // default process timeout (30 min)
        public int HttpRequestTimeout { get; set; } = 60; // HttpClient request timeout in minutes (1 hour for large models)

        // Authentication for LLM API: if AuthToken is set, it will be applied to HttpClient requests.
        // AuthScheme + AuthToken => e.g. "Bearer <token>". If AuthScheme is empty, the raw header value will be used.
        public string AuthScheme { get; set; } = "Bearer";
        public string AuthHeaderName { get; set; } = "Authorization";
        public string AuthToken { get; set; } = string.Empty;

        /// <summary>
        /// Working directory for agent operations. All file operations happen relative to this.
        /// Defaults to "./work" subdirectory of the application directory.
        /// </summary>
        public string WorkDirectory { get; set; } = @"./work";

        // Tool permissions: key is "repoPath:toolName", value indicates if always allowed
        public Dictionary<string, bool> ToolPermissions { get; set; } = new();
        
        /// <summary>
        /// If true, automatically approve all tool calls in TUI mode without prompting.
        /// Default is true for TUI mode usability. Set to false to require manual approval.
        /// </summary>
        public bool AutoApproveTuiTools { get; set; } = true;
        
        public static AgentConfig Config = new();
        
        /// <summary>
        /// Gets the absolute path to the work directory, creating it if needed.
        /// </summary>
        public static string GetWorkDirectory()
        {
            var workDir = Config.WorkDirectory;
            if (!Path.IsPathRooted(workDir))
            {
                workDir = Path.Combine(Directory.GetCurrentDirectory(), workDir);
            }
            workDir = Path.GetFullPath(workDir);
            Directory.CreateDirectory(workDir);
            return workDir;
        }
        public static string GetConfigPath()
        {
            //read from config file in executable dir first

            var localPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            // Allow override via env var
            var env = Environment.GetEnvironmentVariable("LM_AGENT_CONFIG");
            if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env);
            return localPath;
            //var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            //if (string.IsNullOrWhiteSpace(baseDir)) baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            //var dir = Path.Combine(baseDir, "thuvu");
            //Directory.CreateDirectory(dir);
            //return Path.Combine(dir, "config.json");
        }

        public static void LoadConfig()
        {
            var path = GetConfigPath();
            try
            {
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

                    // If your config is stored under a named section in the JSON file (for example "AgentConfig"),
                    // extract that section and pass its raw JSON to the deserializer. Otherwise fall back to deserializing the whole file.
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    // Try multiple section names: "AgentConfig", "Agent"
                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("AgentConfig", out var section))
                    {
                        Config = JsonSerializer.Deserialize<AgentConfig>(section.GetRawText(), options) ?? new AgentConfig();
                        AgentLogger.LogDebug("Loaded AgentConfig from section 'AgentConfig' in {Path}", path);
                    }
                    else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Agent", out var agentSection))
                    {
                        Config = JsonSerializer.Deserialize<AgentConfig>(agentSection.GetRawText(), options) ?? new AgentConfig();
                        AgentLogger.LogDebug("Loaded AgentConfig from section 'Agent' in {Path}", path);
                    }
                    else
                    {
                        // No section found — try deserializing the whole file
                        Config = JsonSerializer.Deserialize<AgentConfig>(json, options) ?? new AgentConfig();
                        AgentLogger.LogDebug("Loaded AgentConfig from root in {Path}", path);
                    }
                    AgentLogger.LogDebug("Loaded Model: {Model}, Host: {Host}", Config.Model, Config.HostUrl);
                }
                else
                {
                    AgentLogger.LogWarning("Config file not found at {Path}, using defaults", path);
                    SaveConfig(); // write defaults
                }
            }
            catch (Exception ex)
            {
                AgentLogger.LogError("Failed to load config from {Path}: {Error}", path, ex.Message);
                Config = new AgentConfig();
            }
        }

        public static bool SaveConfig()
        {
            try
            {
                var path = GetConfigPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var json = JsonSerializer.Serialize(Config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
                return true;
            }
            catch { return false; }
        }

        public static void ApplyConfig(HttpClient http)
        {
            // Update HttpClient and runtime flags from config
            http.BaseAddress = new Uri(Config.HostUrl);
            http.Timeout = TimeSpan.FromMinutes(Config.HttpRequestTimeout);

            // Apply authentication header if a token is configured.
            try
            {
                if (!string.IsNullOrWhiteSpace(Config.AuthToken))
                {
                    if (!string.IsNullOrWhiteSpace(Config.AuthScheme))
                    {
                        // Use AuthenticationHeaderValue when scheme is provided (e.g. Bearer)
                        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(Config.AuthScheme, Config.AuthToken);
                    }
                    else
                    {
                        // Fallback: add raw header value using the configured header name
                        http.DefaultRequestHeaders.Remove(Config.AuthHeaderName);
                        http.DefaultRequestHeaders.TryAddWithoutValidation(Config.AuthHeaderName, Config.AuthToken);
                    }
                }
            }
            catch
            {
                // Ignore header application errors; do not crash the app due to misconfigured header values.
            }
            // Optional: adjust any other defaults that reference timeout/model, etc.
        }

    }
}
