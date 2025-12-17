using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using thuvu.Models;
using thuvu.Tools;

namespace thuvu
{
    /// <summary>
    /// Handlers for RAG-related commands (/rag)
    /// </summary>
    public static class RagCommandHandlers
    {
        // /rag config|enable|disable|stats|index|search|clear
        public static async Task HandleRagCommandAsync(string line, CancellationToken ct)
        {
            var parts = ConsoleHelpers.TokenizeArgs(line);
            if (parts.Count < 2)
            {
                Console.WriteLine("Usage: /rag config|enable|disable|stats|index|search|clear");
                return;
            }

            var subCommand = parts[1].ToLowerInvariant();

            switch (subCommand)
            {
                case "config":
                    Console.WriteLine($"RAG Configuration:");
                    Console.WriteLine($"  Enabled: {RagConfig.Instance.Enabled}");
                    Console.WriteLine($"  Connection: {RagConfig.Instance.ConnectionString}");
                    Console.WriteLine($"  Embedding Dimension: {RagConfig.Instance.EmbeddingDimension}");
                    Console.WriteLine($"  Max Chunk Size: {RagConfig.Instance.MaxChunkSize}");
                    Console.WriteLine($"  Chunk Overlap: {RagConfig.Instance.ChunkOverlap}");
                    Console.WriteLine($"  Top K Results: {RagConfig.Instance.TopK}");
                    Console.WriteLine($"  Similarity Threshold: {RagConfig.Instance.SimilarityThreshold}");
                    Console.WriteLine($"  Config path: {RagConfig.GetConfigPath()}");
                    break;

                case "enable":
                    RagConfig.Instance.Enabled = true;
                    RagConfig.SaveConfig();
                    Console.WriteLine("RAG enabled. Ensure PostgreSQL with pgvector extension is running.");
                    break;

                case "disable":
                    RagConfig.Instance.Enabled = false;
                    RagConfig.SaveConfig();
                    Console.WriteLine("RAG disabled.");
                    break;

                case "stats":
                    try
                    {
                        var statsResult = await RagToolImpl.RagStatsTool("{}", ct);
                        using var statsDoc = JsonDocument.Parse(statsResult);
                        var root = statsDoc.RootElement;
                        if (root.TryGetProperty("enabled", out var enabledEl) && enabledEl.GetBoolean())
                        {
                            Console.WriteLine("RAG Index Statistics:");
                            Console.WriteLine($"  Total Chunks: {root.GetProperty("total_chunks").GetInt64()}");
                            Console.WriteLine($"  Total Sources: {root.GetProperty("total_sources").GetInt64()}");
                            Console.WriteLine($"  Total Characters: {root.GetProperty("total_characters").GetInt64()}");
                        }
                        else
                        {
                            Console.WriteLine("RAG is not enabled. Use '/rag enable' first.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error getting stats: {ex.Message}");
                    }
                    break;

                case "index":
                    if (parts.Count < 3)
                    {
                        Console.WriteLine("Usage: /rag index PATH [--recursive] [--pattern GLOB]");
                        return;
                    }

                    var indexPath = parts[2];
                    bool recursive = false;
                    string pattern = "*.cs";

                    for (int i = 3; i < parts.Count; i++)
                    {
                        if (parts[i].Equals("--recursive", StringComparison.OrdinalIgnoreCase))
                            recursive = true;
                        else if (parts[i].Equals("--pattern", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Count)
                            pattern = parts[++i];
                    }

                    try
                    {
                        var indexArgs = JsonSerializer.Serialize(new { path = indexPath, recursive, pattern });
                        var indexResult = await RagToolImpl.RagIndexTool(indexArgs, ct);
                        using var indexDoc = JsonDocument.Parse(indexResult);
                        var indexRoot = indexDoc.RootElement;

                        if (indexRoot.TryGetProperty("success", out var successEl) && successEl.GetBoolean())
                        {
                            Console.WriteLine($"✅ Indexed {indexRoot.GetProperty("indexed_chunks").GetInt32()} chunks from {indexRoot.GetProperty("indexed_files").GetInt32()} files");
                        }
                        else if (indexRoot.TryGetProperty("error", out var errorEl))
                        {
                            Console.WriteLine($"❌ Error: {errorEl.GetString()}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error indexing: {ex.Message}");
                    }
                    break;

                case "search":
                    if (parts.Count < 3)
                    {
                        Console.WriteLine("Usage: /rag search QUERY [--top N]");
                        return;
                    }

                    var queryParts = new List<string>();
                    int? topK = null;

                    for (int i = 2; i < parts.Count; i++)
                    {
                        if (parts[i].Equals("--top", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Count)
                        {
                            if (int.TryParse(parts[++i], out var k))
                                topK = k;
                        }
                        else
                        {
                            queryParts.Add(parts[i]);
                        }
                    }

                    var query = string.Join(" ", queryParts);

                    try
                    {
                        var searchArgs = topK.HasValue
                            ? JsonSerializer.Serialize(new { query, top_k = topK.Value })
                            : JsonSerializer.Serialize(new { query });
                        var searchResult = await RagToolImpl.RagSearchTool(searchArgs, ct);
                        using var searchDoc = JsonDocument.Parse(searchResult);
                        var searchRoot = searchDoc.RootElement;

                        if (searchRoot.TryGetProperty("results", out var resultsEl))
                        {
                            Console.WriteLine($"Found {searchRoot.GetProperty("count").GetInt32()} results:");
                            Console.WriteLine();

                            foreach (var result in resultsEl.EnumerateArray())
                            {
                                var similarity = result.GetProperty("similarity").GetSingle();
                                var source = result.GetProperty("source").GetString();
                                var content = result.GetProperty("content").GetString();

                                ConsoleHelpers.WithColor(ConsoleColor.Cyan, () => Console.WriteLine($"[{similarity:P1}] {source}"));
                                // Show full content - let it scroll naturally
                                Console.WriteLine(content);
                                Console.WriteLine();
                            }
                        }
                        else if (searchRoot.TryGetProperty("error", out var errorEl))
                        {
                            Console.WriteLine($"❌ Error: {errorEl.GetString()}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error searching: {ex.Message}");
                    }
                    break;

                case "clear":
                    string? clearPath = parts.Count > 2 ? parts[2] : null;

                    try
                    {
                        var clearArgs = clearPath != null
                            ? JsonSerializer.Serialize(new { source_path = clearPath })
                            : "{}";
                        var clearResult = await RagToolImpl.RagClearTool(clearArgs, ct);
                        using var clearDoc = JsonDocument.Parse(clearResult);
                        var clearRoot = clearDoc.RootElement;

                        if (clearRoot.TryGetProperty("success", out var successClearEl) && successClearEl.GetBoolean())
                        {
                            var deleted = clearRoot.GetProperty("deleted_chunks").GetInt32();
                            var scope = clearRoot.GetProperty("scope").GetString();
                            Console.WriteLine($"✅ Cleared {deleted} chunks (scope: {scope})");
                        }
                        else if (clearRoot.TryGetProperty("error", out var errorEl))
                        {
                            Console.WriteLine($"❌ Error: {errorEl.GetString()}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error clearing: {ex.Message}");
                    }
                    break;

                default:
                    Console.WriteLine("Unknown RAG command. Available: config, enable, disable, stats, index, search, clear");
                    break;
            }
        }
    }
}
