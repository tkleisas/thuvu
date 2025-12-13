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

        public static RagConfig Instance { get; private set; } = new();

        public static string GetConfigPath()
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(baseDir)) 
                baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(baseDir, "thuvu");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "rag_config.json");
        }

        public static void LoadConfig()
        {
            var path = GetConfigPath();
            try
            {
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    Instance = JsonSerializer.Deserialize<RagConfig>(json) ?? new RagConfig();
                }
                else
                {
                    SaveConfig();
                }
            }
            catch 
            { 
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
