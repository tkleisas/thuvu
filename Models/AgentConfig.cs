using System;
using System.Collections.Generic;
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
        
        /// <summary>
        /// Working directory for agent operations. All file operations happen relative to this.
        /// Defaults to "./work" subdirectory of the application directory.
        /// </summary>
        public string WorkDirectory { get; set; } = @"C:\Users\tasos\Documents\projects\mandelbrot";

        // Tool permissions: key is "repoPath:toolName", value indicates if always allowed
        public Dictionary<string, bool> ToolPermissions { get; set; } = new();
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
            // Allow override via env var
            var env = Environment.GetEnvironmentVariable("LM_AGENT_CONFIG");
            if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env);

            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(baseDir)) baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(baseDir, "thuvu");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "config.json");
        }

        public static void LoadConfig()
        {
            var path = GetConfigPath();
            try
            {
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    Config = JsonSerializer.Deserialize<AgentConfig>(json) ?? new AgentConfig();
                }
                else
                {
                    SaveConfig(); // write defaults
                }
            }
            catch { Config = new AgentConfig(); }
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
                                              // Optional: adjust any other defaults that reference timeout/model, etc.
        }

    }
}
