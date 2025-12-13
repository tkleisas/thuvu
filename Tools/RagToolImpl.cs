using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using thuvu.Models;

namespace thuvu.Tools
{
    /// <summary>
    /// Tool implementations for RAG operations.
    /// </summary>
    public static class RagToolImpl
    {
        private static RagService? _ragService;
        private static HttpClient? _httpClient;

        public static void Initialize(HttpClient http)
        {
            _httpClient = http;
            _ragService = new RagService(http);
        }

        /// <summary>
        /// Index a file or directory for RAG retrieval.
        /// </summary>
        public static async Task<string> RagIndexTool(string argsJson, CancellationToken ct = default)
        {
            if (_ragService == null)
                return JsonSerializer.Serialize(new { error = "RAG service not initialized" });

            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                var root = doc.RootElement;

                var path = root.TryGetProperty("path", out var pathEl) ? pathEl.GetString() : null;
                var recursive = root.TryGetProperty("recursive", out var recEl) && recEl.GetBoolean();
                var pattern = root.TryGetProperty("pattern", out var patEl) ? patEl.GetString() : "*.cs";

                if (string.IsNullOrWhiteSpace(path))
                    return JsonSerializer.Serialize(new { error = "path is required" });

                var fullPath = Path.GetFullPath(path);
                int totalIndexed = 0;
                var indexedFiles = new List<string>();

                if (Directory.Exists(fullPath))
                {
                    var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                    // Directory.GetFiles doesn't support glob patterns like **/*.cs, only simple patterns like *.cs
                    var simplePattern = pattern ?? "*";
                    if (simplePattern.StartsWith("**/"))
                        simplePattern = simplePattern.Substring(3);
                    if (simplePattern.Contains("/") || simplePattern.Contains("\\"))
                        simplePattern = Path.GetFileName(simplePattern); // Get just the filename pattern
                    var files = Directory.GetFiles(fullPath, simplePattern, searchOption);

                    foreach (var file in files)
                    {
                        try
                        {
                            var content = await File.ReadAllTextAsync(file, ct);
                            var metadata = new Dictionary<string, object>
                            {
                                { "file_extension", Path.GetExtension(file) },
                                { "file_name", Path.GetFileName(file) },
                                { "indexed_at", DateTime.UtcNow.ToString("o") }
                            };

                            var count = await _ragService.IndexDocumentAsync(file, content, metadata, ct);
                            totalIndexed += count;
                            indexedFiles.Add(file);
                        }
                        catch (Exception ex)
                        {
                            AgentLogger.LogWarning("Failed to index {File}: {Error}", file, ex.Message);
                        }
                    }
                }
                else if (File.Exists(fullPath))
                {
                    var content = await File.ReadAllTextAsync(fullPath, ct);
                    var metadata = new Dictionary<string, object>
                    {
                        { "file_extension", Path.GetExtension(fullPath) },
                        { "file_name", Path.GetFileName(fullPath) },
                        { "indexed_at", DateTime.UtcNow.ToString("o") }
                    };

                    totalIndexed = await _ragService.IndexDocumentAsync(fullPath, content, metadata, ct);
                    indexedFiles.Add(fullPath);
                }
                else
                {
                    return JsonSerializer.Serialize(new { error = $"Path not found: {fullPath}" });
                }

                return JsonSerializer.Serialize(new
                {
                    success = true,
                    indexed_chunks = totalIndexed,
                    indexed_files = indexedFiles.Count,
                    files = indexedFiles
                });
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { error = ex.Message });
            }
        }

        /// <summary>
        /// Search the RAG index for relevant content.
        /// </summary>
        public static async Task<string> RagSearchTool(string argsJson, CancellationToken ct = default)
        {
            if (_ragService == null)
                return JsonSerializer.Serialize(new { error = "RAG service not initialized" });

            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                var root = doc.RootElement;

                var query = root.TryGetProperty("query", out var queryEl) ? queryEl.GetString() : null;
                var topK = root.TryGetProperty("top_k", out var kEl) ? kEl.GetInt32() : (int?)null;

                if (string.IsNullOrWhiteSpace(query))
                    return JsonSerializer.Serialize(new { error = "query is required" });

                var results = await _ragService.SearchAsync(query, topK, ct);

                return JsonSerializer.Serialize(new
                {
                    success = true,
                    count = results.Count,
                    results = results.ConvertAll(r => new
                    {
                        source = r.SourcePath,
                        chunk = r.ChunkIndex,
                        content = r.Content,
                        similarity = r.Similarity
                    })
                });
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { error = ex.Message });
            }
        }

        /// <summary>
        /// Clear the RAG index.
        /// </summary>
        public static async Task<string> RagClearTool(string argsJson, CancellationToken ct = default)
        {
            if (_ragService == null)
                return JsonSerializer.Serialize(new { error = "RAG service not initialized" });

            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                var root = doc.RootElement;

                var sourcePath = root.TryGetProperty("source_path", out var pathEl) ? pathEl.GetString() : null;

                var deleted = await _ragService.ClearAsync(sourcePath, ct);

                return JsonSerializer.Serialize(new
                {
                    success = true,
                    deleted_chunks = deleted,
                    scope = sourcePath ?? "all"
                });
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { error = ex.Message });
            }
        }

        /// <summary>
        /// Get RAG index statistics.
        /// </summary>
        public static async Task<string> RagStatsTool(string argsJson, CancellationToken ct = default)
        {
            if (_ragService == null)
                return JsonSerializer.Serialize(new { error = "RAG service not initialized" });

            try
            {
                var stats = await _ragService.GetStatsAsync(ct);

                return JsonSerializer.Serialize(new
                {
                    enabled = stats.Enabled,
                    total_chunks = stats.TotalChunks,
                    total_sources = stats.TotalSources,
                    total_characters = stats.TotalCharacters
                });
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { error = ex.Message });
            }
        }
    }
}
