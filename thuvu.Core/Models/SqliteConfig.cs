using System;
using System.IO;
using System.Text.Json;

namespace thuvu.Models
{
    /// <summary>
    /// Configuration for SQLite-based code indexing and context storage.
    /// </summary>
    public sealed class SqliteConfig
    {
        /// <summary>
        /// Path to the SQLite database file. Defaults to thuvu.db in the work directory.
        /// </summary>
        public string DatabasePath { get; set; } = "";

        /// <summary>
        /// Enable/disable code indexing features.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// File extensions to index for code symbols.
        /// </summary>
        public string[] IndexExtensions { get; set; } = new[] { ".cs", ".ts", ".js", ".py", ".go", ".java" };

        /// <summary>
        /// Directories to exclude from indexing.
        /// </summary>
        public string[] ExcludeDirectories { get; set; } = new[] { "bin", "obj", "node_modules", ".git", ".vs", "packages" };

        /// <summary>
        /// Maximum file size to index (in bytes). Default 1MB.
        /// </summary>
        public long MaxFileSizeBytes { get; set; } = 1024 * 1024;

        /// <summary>
        /// How long to keep context entries (in days). 0 = forever.
        /// </summary>
        public int ContextRetentionDays { get; set; } = 30;

        public static SqliteConfig Instance { get; private set; } = new();

        /// <summary>
        /// Get the effective database path, resolving relative paths.
        /// </summary>
        public string GetEffectiveDatabasePath()
        {
            if (!string.IsNullOrEmpty(DatabasePath))
                return Path.GetFullPath(DatabasePath);

            // Default: thuvu.db in work directory or executable directory
            var workDir = AgentConfig.Config?.WorkDirectory;
            if (!string.IsNullOrEmpty(workDir) && Directory.Exists(workDir))
                return Path.Combine(Path.GetFullPath(workDir), "thuvu.db");

            return Path.Combine(AppContext.BaseDirectory, "thuvu.db");
        }

        public static string GetConfigPath()
        {
            var localPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            var env = Environment.GetEnvironmentVariable("LM_AGENT_CONFIG");
            if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env);
            return localPath;
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

                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("SqliteConfig", out var section))
                    {
                        Instance = JsonSerializer.Deserialize<SqliteConfig>(section.GetRawText(), options) ?? new SqliteConfig();
                        AgentLogger.LogInfo("SQLite config loaded, Enabled={Enabled}, Path={Path}", 
                            Instance.Enabled, Instance.GetEffectiveDatabasePath());
                    }
                    else
                    {
                        Instance = new SqliteConfig();
                        AgentLogger.LogInfo("SQLite config using defaults, Path={Path}", Instance.GetEffectiveDatabasePath());
                    }
                }
                else
                {
                    Instance = new SqliteConfig();
                }
            }
            catch (Exception ex)
            {
                AgentLogger.LogError("Failed to load SqliteConfig: {Error}", ex.Message);
                Instance = new SqliteConfig();
            }
        }
    }
}
