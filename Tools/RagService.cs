using Npgsql;
using Pgvector;
using Pgvector.Npgsql;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using thuvu.Models;

namespace thuvu.Tools
{
    /// <summary>
    /// RAG (Retrieval-Augmented Generation) service using PostgreSQL with pgvector.
    /// Handles document indexing, embedding generation, and semantic search.
    /// </summary>
    public class RagService
    {
        private readonly HttpClient _http;
        private static bool _initialized = false;

        public RagService(HttpClient http)
        {
            _http = http;
        }

        /// <summary>
        /// Initialize the database schema for RAG storage.
        /// Creates the pgvector extension and documents table if they don't exist.
        /// </summary>
        public async Task InitializeDatabaseAsync(CancellationToken ct = default)
        {
            if (!RagConfig.Instance.Enabled)
            {
                AgentLogger.LogWarning("RAG is disabled. Enable it in rag_config.json");
                return;
            }

            await using var conn = new NpgsqlConnection(RagConfig.Instance.ConnectionString);
            
            // Register pgvector types
            var dataSourceBuilder = new NpgsqlDataSourceBuilder(RagConfig.Instance.ConnectionString);
            dataSourceBuilder.UseVector();
            await using var dataSource = dataSourceBuilder.Build();
            await using var cmd = dataSource.CreateCommand();

            await conn.OpenAsync(ct);

            // Create pgvector extension
            await using (var extCmd = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS vector", conn))
            {
                await extCmd.ExecuteNonQueryAsync(ct);
            }

            // Create documents table with vector column
            var createTableSql = $@"
                CREATE TABLE IF NOT EXISTS rag_documents (
                    id SERIAL PRIMARY KEY,
                    source_path TEXT NOT NULL,
                    chunk_index INTEGER NOT NULL,
                    content TEXT NOT NULL,
                    embedding vector({RagConfig.Instance.EmbeddingDimension}),
                    metadata JSONB,
                    created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
                    UNIQUE(source_path, chunk_index)
                );
                
                CREATE INDEX IF NOT EXISTS idx_rag_documents_embedding 
                ON rag_documents USING ivfflat (embedding vector_cosine_ops)
                WITH (lists = 100);
                
                CREATE INDEX IF NOT EXISTS idx_rag_documents_source 
                ON rag_documents (source_path);
            ";

            await using (var tableCmd = new NpgsqlCommand(createTableSql, conn))
            {
                await tableCmd.ExecuteNonQueryAsync(ct);
            }

            _initialized = true;
            AgentLogger.LogInfo("RAG database initialized successfully");
        }

        /// <summary>
        /// Generate embeddings for text using the LLM API.
        /// </summary>
        public async Task<float[]?> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
        {
            try
            {
                var request = new
                {
                    model = AgentConfig.Config.Model,
                    input = text
                };

                using var response = await _http.PostAsJsonAsync("/v1/embeddings", request, ct);
                
                if (!response.IsSuccessStatusCode)
                {
                    AgentLogger.LogWarning("Embedding API returned {StatusCode}", response.StatusCode);
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                
                var data = doc.RootElement.GetProperty("data")[0];
                var embeddingArray = data.GetProperty("embedding");
                
                var embedding = new float[embeddingArray.GetArrayLength()];
                int i = 0;
                foreach (var elem in embeddingArray.EnumerateArray())
                {
                    embedding[i++] = elem.GetSingle();
                }
                
                return embedding;
            }
            catch (Exception ex)
            {
                AgentLogger.LogError(ex, "Failed to generate embedding");
                return null;
            }
        }

        /// <summary>
        /// Split text into chunks for indexing.
        /// </summary>
        public List<string> ChunkText(string text)
        {
            var chunks = new List<string>();
            var maxSize = RagConfig.Instance.MaxChunkSize;
            var overlap = RagConfig.Instance.ChunkOverlap;

            if (string.IsNullOrEmpty(text) || text.Length <= maxSize)
            {
                chunks.Add(text);
                return chunks;
            }

            int start = 0;
            while (start < text.Length)
            {
                int end = Math.Min(start + maxSize, text.Length);
                
                // Try to break at a sentence or word boundary
                if (end < text.Length)
                {
                    int lastPeriod = text.LastIndexOf('.', end, Math.Min(end - start, 100));
                    int lastNewline = text.LastIndexOf('\n', end, Math.Min(end - start, 100));
                    int lastSpace = text.LastIndexOf(' ', end, Math.Min(end - start, 50));
                    
                    int breakPoint = Math.Max(lastPeriod, Math.Max(lastNewline, lastSpace));
                    if (breakPoint > start + maxSize / 2)
                        end = breakPoint + 1;
                }

                chunks.Add(text.Substring(start, end - start).Trim());
                start = end - overlap;
                if (start < 0) start = 0;
            }

            return chunks;
        }

        /// <summary>
        /// Index a document by chunking it and storing embeddings.
        /// </summary>
        public async Task<int> IndexDocumentAsync(string sourcePath, string content, Dictionary<string, object>? metadata = null, CancellationToken ct = default)
        {
            if (!RagConfig.Instance.Enabled)
                throw new InvalidOperationException("RAG is not enabled");

            if (!_initialized)
                await InitializeDatabaseAsync(ct);

            var chunks = ChunkText(content);
            int indexed = 0;

            var dataSourceBuilder = new NpgsqlDataSourceBuilder(RagConfig.Instance.ConnectionString);
            dataSourceBuilder.UseVector();
            await using var dataSource = dataSourceBuilder.Build();
            await using var conn = await dataSource.OpenConnectionAsync(ct);

            // Delete existing chunks for this source
            await using (var deleteCmd = new NpgsqlCommand(
                "DELETE FROM rag_documents WHERE source_path = @path", conn))
            {
                deleteCmd.Parameters.AddWithValue("path", sourcePath);
                await deleteCmd.ExecuteNonQueryAsync(ct);
            }

            for (int i = 0; i < chunks.Count; i++)
            {
                var embedding = await GenerateEmbeddingAsync(chunks[i], ct);
                if (embedding == null)
                {
                    AgentLogger.LogWarning("Failed to generate embedding for chunk {Index} of {Path}", i, sourcePath);
                    continue;
                }

                var metadataJson = metadata != null ? JsonSerializer.Serialize(metadata) : null;

                await using var cmd = new NpgsqlCommand(@"
                    INSERT INTO rag_documents (source_path, chunk_index, content, embedding, metadata)
                    VALUES (@path, @index, @content, @embedding, @metadata::jsonb)
                    ON CONFLICT (source_path, chunk_index) 
                    DO UPDATE SET content = @content, embedding = @embedding, metadata = @metadata::jsonb
                ", conn);

                cmd.Parameters.AddWithValue("path", sourcePath);
                cmd.Parameters.AddWithValue("index", i);
                cmd.Parameters.AddWithValue("content", chunks[i]);
                cmd.Parameters.AddWithValue("embedding", new Vector(embedding));
                cmd.Parameters.AddWithValue("metadata", (object?)metadataJson ?? DBNull.Value);

                await cmd.ExecuteNonQueryAsync(ct);
                indexed++;
            }

            AgentLogger.LogInfo("Indexed {Count} chunks from {Path}", indexed, sourcePath);
            return indexed;
        }

        /// <summary>
        /// Search for similar documents using vector similarity.
        /// </summary>
        public async Task<List<RagSearchResult>> SearchAsync(string query, int? topK = null, CancellationToken ct = default)
        {
            if (!RagConfig.Instance.Enabled)
                throw new InvalidOperationException("RAG is not enabled");

            if (!_initialized)
                await InitializeDatabaseAsync(ct);

            var queryEmbedding = await GenerateEmbeddingAsync(query, ct);
            if (queryEmbedding == null)
            {
                AgentLogger.LogWarning("Failed to generate query embedding");
                return new List<RagSearchResult>();
            }

            var results = new List<RagSearchResult>();
            var k = topK ?? RagConfig.Instance.TopK;

            var dataSourceBuilder = new NpgsqlDataSourceBuilder(RagConfig.Instance.ConnectionString);
            dataSourceBuilder.UseVector();
            await using var dataSource = dataSourceBuilder.Build();
            await using var conn = await dataSource.OpenConnectionAsync(ct);

            await using var cmd = new NpgsqlCommand(@"
                SELECT source_path, chunk_index, content, metadata,
                       1 - (embedding <=> @embedding) as similarity
                FROM rag_documents
                WHERE 1 - (embedding <=> @embedding) >= @threshold
                ORDER BY embedding <=> @embedding
                LIMIT @limit
            ", conn);

            cmd.Parameters.AddWithValue("embedding", new Vector(queryEmbedding));
            cmd.Parameters.AddWithValue("threshold", RagConfig.Instance.SimilarityThreshold);
            cmd.Parameters.AddWithValue("limit", k);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(new RagSearchResult
                {
                    SourcePath = reader.GetString(0),
                    ChunkIndex = reader.GetInt32(1),
                    Content = reader.GetString(2),
                    Metadata = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Similarity = reader.GetFloat(4)
                });
            }

            AgentLogger.LogInfo("RAG search returned {Count} results for query", results.Count);
            return results;
        }

        /// <summary>
        /// Clear all indexed documents or documents from a specific source.
        /// </summary>
        public async Task<int> ClearAsync(string? sourcePath = null, CancellationToken ct = default)
        {
            if (!RagConfig.Instance.Enabled)
                throw new InvalidOperationException("RAG is not enabled");

            await using var conn = new NpgsqlConnection(RagConfig.Instance.ConnectionString);
            await conn.OpenAsync(ct);

            string sql = sourcePath != null
                ? "DELETE FROM rag_documents WHERE source_path = @path"
                : "DELETE FROM rag_documents";

            await using var cmd = new NpgsqlCommand(sql, conn);
            if (sourcePath != null)
                cmd.Parameters.AddWithValue("path", sourcePath);

            var deleted = await cmd.ExecuteNonQueryAsync(ct);
            AgentLogger.LogInfo("Cleared {Count} RAG documents", deleted);
            return deleted;
        }

        /// <summary>
        /// Get statistics about the RAG index.
        /// </summary>
        public async Task<RagStats> GetStatsAsync(CancellationToken ct = default)
        {
            if (!RagConfig.Instance.Enabled)
                return new RagStats { Enabled = false };

            await using var conn = new NpgsqlConnection(RagConfig.Instance.ConnectionString);
            await conn.OpenAsync(ct);

            await using var cmd = new NpgsqlCommand(@"
                SELECT 
                    COUNT(*) as total_chunks,
                    COUNT(DISTINCT source_path) as total_sources,
                    COALESCE(SUM(LENGTH(content)), 0) as total_chars
                FROM rag_documents
            ", conn);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                return new RagStats
                {
                    Enabled = true,
                    TotalChunks = reader.GetInt64(0),
                    TotalSources = reader.GetInt64(1),
                    TotalCharacters = reader.GetInt64(2)
                };
            }

            return new RagStats { Enabled = true };
        }
    }

    public class RagSearchResult
    {
        public string SourcePath { get; set; } = "";
        public int ChunkIndex { get; set; }
        public string Content { get; set; } = "";
        public string? Metadata { get; set; }
        public float Similarity { get; set; }
    }

    public class RagStats
    {
        public bool Enabled { get; set; }
        public long TotalChunks { get; set; }
        public long TotalSources { get; set; }
        public long TotalCharacters { get; set; }
    }
}
