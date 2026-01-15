using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using thuvu.Models;

namespace thuvu.Tools
{
    /// <summary>
    /// Tool implementations for SQLite-based code indexing and context storage.
    /// </summary>
    public static class SqliteToolImpl
    {
        private static readonly CodeIndexer _indexer = new();
        private static readonly JsonSerializerOptions _jsonOptions = new() 
        { 
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        /// <summary>
        /// Index code files in a directory or single file.
        /// </summary>
        public static async Task<string> CodeIndexAsync(string path, bool force = false, CancellationToken ct = default)
        {
            try
            {
                if (!SqliteConfig.Instance.Enabled)
                {
                    return JsonSerializer.Serialize(new { success = false, error = "SQLite indexing is disabled" }, _jsonOptions);
                }

                var fullPath = Path.GetFullPath(path);

                if (Directory.Exists(fullPath))
                {
                    var result = await _indexer.IndexDirectoryAsync(fullPath, force, ct);
                    return JsonSerializer.Serialize(new
                    {
                        success = true,
                        path = fullPath,
                        totalFiles = result.TotalFiles,
                        indexedFiles = result.IndexedFiles,
                        skippedFiles = result.SkippedFiles,
                        errors = result.Errors.Count > 0 ? result.Errors.Take(10).ToList() : null
                    }, _jsonOptions);
                }
                else if (File.Exists(fullPath))
                {
                    var indexed = await _indexer.IndexFileAsync(fullPath, force, ct);
                    return JsonSerializer.Serialize(new
                    {
                        success = true,
                        path = fullPath,
                        indexed,
                        message = indexed ? "File indexed successfully" : "File unchanged, skipped"
                    }, _jsonOptions);
                }
                else
                {
                    return JsonSerializer.Serialize(new { success = false, error = $"Path not found: {path}" }, _jsonOptions);
                }
            }
            catch (Exception ex)
            {
                AgentLogger.LogError(ex, "code_index failed");
                return JsonSerializer.Serialize(new { success = false, error = ex.Message }, _jsonOptions);
            }
        }

        /// <summary>
        /// Query indexed code symbols.
        /// </summary>
        public static async Task<string> CodeQueryAsync(
            string? search = null,
            string? kind = null,
            string? file = null,
            long? symbolId = null,
            bool findReferences = false,
            int limit = 50,
            CancellationToken ct = default)
        {
            try
            {
                if (!SqliteConfig.Instance.Enabled)
                {
                    return JsonSerializer.Serialize(new { success = false, error = "SQLite indexing is disabled" }, _jsonOptions);
                }

                var db = SqliteService.Instance;

                // Get specific symbol by ID
                if (symbolId.HasValue)
                {
                    var symbol = await db.GetSymbolAsync(symbolId.Value, ct);
                    if (symbol == null)
                    {
                        return JsonSerializer.Serialize(new { success = false, error = $"Symbol not found: {symbolId}" }, _jsonOptions);
                    }

                    if (findReferences)
                    {
                        var refs = await db.FindReferencesAsync(symbolId.Value, ct);
                        return JsonSerializer.Serialize(new
                        {
                            success = true,
                            symbol = FormatSymbol(symbol),
                            references = refs.Select(r => new
                            {
                                file = r.FilePath,
                                line = r.Line,
                                column = r.Column,
                                context = r.Context,
                                kind = r.ReferenceKind
                            })
                        }, _jsonOptions);
                    }

                    return JsonSerializer.Serialize(new { success = true, symbol = FormatSymbol(symbol) }, _jsonOptions);
                }

                // Get all symbols in a file
                if (!string.IsNullOrEmpty(file) && string.IsNullOrEmpty(search))
                {
                    var fullPath = Path.GetFullPath(file);
                    var symbols = await db.GetSymbolsInFileAsync(fullPath, ct);
                    return JsonSerializer.Serialize(new
                    {
                        success = true,
                        file = fullPath,
                        count = symbols.Count,
                        symbols = symbols.Select(FormatSymbol)
                    }, _jsonOptions);
                }

                // Search symbols
                if (!string.IsNullOrEmpty(search))
                {
                    var symbols = await db.SearchSymbolsAsync(search, kind, file, limit, ct);
                    return JsonSerializer.Serialize(new
                    {
                        success = true,
                        query = search,
                        kind,
                        count = symbols.Count,
                        symbols = symbols.Select(FormatSymbol)
                    }, _jsonOptions);
                }

                // No search criteria - return stats
                var stats = await db.GetStatsAsync(ct);
                return JsonSerializer.Serialize(new
                {
                    success = true,
                    message = "Provide 'search', 'file', or 'symbol_id' to query symbols",
                    stats = new
                    {
                        totalSymbols = stats.TotalSymbols,
                        totalFiles = stats.TotalFiles,
                        totalReferences = stats.TotalReferences,
                        symbolsByKind = stats.SymbolsByKind,
                        databaseSize = FormatSize(stats.DatabaseSizeBytes)
                    }
                }, _jsonOptions);
            }
            catch (Exception ex)
            {
                AgentLogger.LogError(ex, "code_query failed");
                return JsonSerializer.Serialize(new { success = false, error = ex.Message }, _jsonOptions);
            }
        }

        /// <summary>
        /// Store context/memory for later retrieval.
        /// </summary>
        public static async Task<string> ContextStoreAsync(
            string key,
            string value,
            string? category = null,
            string? projectPath = null,
            int? expiresInDays = null,
            CancellationToken ct = default)
        {
            try
            {
                if (!SqliteConfig.Instance.Enabled)
                {
                    return JsonSerializer.Serialize(new { success = false, error = "SQLite is disabled" }, _jsonOptions);
                }

                var db = SqliteService.Instance;
                var id = await db.StoreContextAsync(key, value, category, projectPath, null, expiresInDays, ct);

                return JsonSerializer.Serialize(new
                {
                    success = true,
                    id,
                    key,
                    category,
                    expiresInDays
                }, _jsonOptions);
            }
            catch (Exception ex)
            {
                AgentLogger.LogError(ex, "context_store failed");
                return JsonSerializer.Serialize(new { success = false, error = ex.Message }, _jsonOptions);
            }
        }

        /// <summary>
        /// Retrieve stored context/memory.
        /// </summary>
        public static async Task<string> ContextGetAsync(
            string? keyPattern = null,
            string? category = null,
            string? projectPath = null,
            int limit = 50,
            CancellationToken ct = default)
        {
            try
            {
                if (!SqliteConfig.Instance.Enabled)
                {
                    return JsonSerializer.Serialize(new { success = false, error = "SQLite is disabled" }, _jsonOptions);
                }

                var db = SqliteService.Instance;
                var entries = await db.GetContextAsync(keyPattern, category, projectPath, limit, ct);

                return JsonSerializer.Serialize(new
                {
                    success = true,
                    count = entries.Count,
                    entries = entries.Select(e => new
                    {
                        id = e.Id,
                        key = e.Key,
                        value = e.Value,
                        category = e.Category,
                        createdAt = e.CreatedAt,
                        expiresAt = e.ExpiresAt
                    })
                }, _jsonOptions);
            }
            catch (Exception ex)
            {
                AgentLogger.LogError(ex, "context_get failed");
                return JsonSerializer.Serialize(new { success = false, error = ex.Message }, _jsonOptions);
            }
        }

        /// <summary>
        /// Get index statistics.
        /// </summary>
        public static async Task<string> IndexStatsAsync(CancellationToken ct = default)
        {
            try
            {
                if (!SqliteConfig.Instance.Enabled)
                {
                    return JsonSerializer.Serialize(new { success = false, enabled = false }, _jsonOptions);
                }

                var db = SqliteService.Instance;
                var stats = await db.GetStatsAsync(ct);

                return JsonSerializer.Serialize(new
                {
                    success = true,
                    enabled = true,
                    databasePath = SqliteConfig.Instance.GetEffectiveDatabasePath(),
                    totalSymbols = stats.TotalSymbols,
                    totalFiles = stats.TotalFiles,
                    totalReferences = stats.TotalReferences,
                    totalContextEntries = stats.TotalContextEntries,
                    databaseSize = FormatSize(stats.DatabaseSizeBytes),
                    symbolsByKind = stats.SymbolsByKind
                }, _jsonOptions);
            }
            catch (Exception ex)
            {
                AgentLogger.LogError(ex, "index_stats failed");
                return JsonSerializer.Serialize(new { success = false, error = ex.Message }, _jsonOptions);
            }
        }

        /// <summary>
        /// Clear indexed data.
        /// </summary>
        public static async Task<string> IndexClearAsync(CancellationToken ct = default)
        {
            try
            {
                if (!SqliteConfig.Instance.Enabled)
                {
                    return JsonSerializer.Serialize(new { success = false, error = "SQLite is disabled" }, _jsonOptions);
                }

                var db = SqliteService.Instance;
                var deleted = await db.ClearAllAsync(ct);

                return JsonSerializer.Serialize(new
                {
                    success = true,
                    deletedRecords = deleted
                }, _jsonOptions);
            }
            catch (Exception ex)
            {
                AgentLogger.LogError(ex, "index_clear failed");
                return JsonSerializer.Serialize(new { success = false, error = ex.Message }, _jsonOptions);
            }
        }

        private static object FormatSymbol(CodeSymbol s)
        {
            return new
            {
                id = s.Id,
                name = s.Name,
                fullName = s.FullName,
                kind = s.Kind,
                file = s.FilePath,
                line = s.LineStart,
                lineEnd = s.LineEnd,
                signature = s.Signature,
                returnType = s.ReturnType,
                visibility = s.Visibility,
                isStatic = s.IsStatic,
                documentation = s.Documentation
            };
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
        }
    }
}
