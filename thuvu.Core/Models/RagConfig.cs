using System;
using System.Text.Json;
using System.IO;

namespace thuvu.Models
{
    /// <summary>
    /// Configuration for RAG (Retrieval-Augmented Generation) with PostgreSQL.
    /// </summary>
    public sealed class RagConfig
    {
        public string ConnectionString { get; set; } = "Host=localhost;Port=5432;Database=thuvu_rag;Username=thuvu;Password=thuvu123";
        public int EmbeddingDimension { get; set; } = 1536; // Default for many embedding models
        public int MaxChunkSize { get; set; } = 1000; // Characters per chunk
        public int ChunkOverlap { get; set; } = 200; // Overlap between chunks
        public int TopK { get; set; } = 5; // Number of results to retrieve
        public float SimilarityThreshold { get; set; } = 0.7f; // Minimum similarity score
        public bool Enabled { get; set; } = false; // RAG disabled by default until configured
        
        /// <summary>
        /// Separate endpoint for embedding API. If empty, uses the main LLM HostUrl.
        /// Useful when using a local embedding model while using a remote LLM.
        /// </summary>
        public string EmbeddingHostUrl { get; set; } = "";
        
        /// <summary>
        /// Model name for embeddings. If empty, uses the main model.
        /// Common values: "text-embedding-ada-002", "nomic-embed-text", etc.
        /// </summary>
        public string EmbeddingModel { get; set; } = "";

        public static RagConfig Instance { get; private set; } = new();

        public static string GetConfigPath()
        {
            //read from config file in executable dir first
            var localPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            // Allow override via env var
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

                    // If your config is stored under a named section in the JSON file (for example "RagConfig"),
                    // extract that section and pass its raw JSON to the deserializer. Otherwise fall back to deserializing the whole file.
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    // Try multiple section names: "RagConfig", "Rag"
                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("RagConfig", out var section))
                    {
                        Instance = JsonSerializer.Deserialize<RagConfig>(section.GetRawText(), options) ?? new RagConfig();
                        AgentLogger.LogInfo("RAG config loaded from RagConfig section, Enabled={Enabled}", Instance.Enabled);
                    }
                    else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Rag", out var ragSection))
                    {
                        Instance = JsonSerializer.Deserialize<RagConfig>(ragSection.GetRawText(), options) ?? new RagConfig();
                        AgentLogger.LogInfo("RAG config loaded from Rag section, Enabled={Enabled}", Instance.Enabled);
                    }
                    else
                    {
                        Instance = JsonSerializer.Deserialize<RagConfig>(json, options) ?? new RagConfig();
                        AgentLogger.LogInfo("RAG config loaded from root, Enabled={Enabled}", Instance.Enabled);
                    }
                }
                else
                {
                    SaveConfig();
                }
            }
            catch (Exception ex)
            {
                AgentLogger.LogError("Failed to load RagConfig: {Error}", ex.Message);
                Instance = new RagConfig();
            }
        }

        public static bool SaveConfig()
        {
            try
            {
                var path = GetConfigPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var json = JsonSerializer.Serialize(Instance, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json);
                return true;
            }
            catch 
            { 
                return false; 
            }
        }
    }
}
