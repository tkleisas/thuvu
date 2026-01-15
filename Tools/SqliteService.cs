using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using thuvu.Models;

namespace thuvu.Tools
{
    /// <summary>
    /// SQLite service for code indexing and context storage.
    /// Provides structured queries for code symbols, references, and project context.
    /// </summary>
    public class SqliteService
    {
        private static SqliteService? _instance;
        private static readonly object _lock = new();
        private string _connectionString = "";
        private bool _initialized = false;

        public static SqliteService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        _instance ??= new SqliteService();
                    }
                }
                return _instance;
            }
        }

        private SqliteService() { }

        /// <summary>
        /// Initialize the database, creating tables if they don't exist.
        /// </summary>
        public async Task InitializeAsync(CancellationToken ct = default)
        {
            if (_initialized) return;

            var dbPath = SqliteConfig.Instance.GetEffectiveDatabasePath();
            var dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            _connectionString = $"Data Source={dbPath}";

            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);

            // Create tables
            var schema = @"
                -- Code symbols (functions, classes, properties, etc.)
                CREATE TABLE IF NOT EXISTS symbols (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    name TEXT NOT NULL,
                    full_name TEXT,
                    kind TEXT NOT NULL,  -- 'function', 'class', 'property', 'field', 'interface', 'enum', 'method'
                    file_path TEXT NOT NULL,
                    line_start INTEGER,
                    line_end INTEGER,
                    column_start INTEGER,
                    signature TEXT,
                    documentation TEXT,
                    parent_id INTEGER REFERENCES symbols(id),
                    visibility TEXT,  -- 'public', 'private', 'protected', 'internal'
                    is_static INTEGER DEFAULT 0,
                    return_type TEXT,
                    last_indexed TEXT
                );

                -- Symbol references (where symbols are used)
                CREATE TABLE IF NOT EXISTS ""references"" (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    symbol_id INTEGER REFERENCES symbols(id) ON DELETE CASCADE,
                    file_path TEXT NOT NULL,
                    line INTEGER,
                    column INTEGER,
                    context TEXT,  -- surrounding code snippet
                    reference_kind TEXT  -- 'call', 'read', 'write', 'type', 'inherit'
                );

                -- Project context/memory
                CREATE TABLE IF NOT EXISTS context (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    project_path TEXT,
                    key TEXT NOT NULL,
                    value TEXT,
                    category TEXT,  -- 'decision', 'pattern', 'preference', 'note', 'error'
                    created_at TEXT DEFAULT (datetime('now')),
                    expires_at TEXT,
                    session_id TEXT
                );

                -- File metadata for change detection
                CREATE TABLE IF NOT EXISTS files (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    path TEXT UNIQUE NOT NULL,
                    hash TEXT,
                    size INTEGER,
                    last_indexed TEXT,
                    symbol_count INTEGER DEFAULT 0
                );

                -- Indexes for performance
                CREATE INDEX IF NOT EXISTS idx_symbols_name ON symbols(name);
                CREATE INDEX IF NOT EXISTS idx_symbols_kind ON symbols(kind);
                CREATE INDEX IF NOT EXISTS idx_symbols_file ON symbols(file_path);
                CREATE INDEX IF NOT EXISTS idx_symbols_parent ON symbols(parent_id);
                CREATE INDEX IF NOT EXISTS idx_references_symbol ON ""references""(symbol_id);
                CREATE INDEX IF NOT EXISTS idx_references_file ON ""references""(file_path);
                CREATE INDEX IF NOT EXISTS idx_context_key ON context(key);
                CREATE INDEX IF NOT EXISTS idx_context_category ON context(category);
                CREATE INDEX IF NOT EXISTS idx_context_project ON context(project_path);
                CREATE INDEX IF NOT EXISTS idx_files_path ON files(path);
            ";

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = schema;
            await cmd.ExecuteNonQueryAsync(ct);

            _initialized = true;
            AgentLogger.LogInfo("SQLite database initialized at {Path}", dbPath);
        }

        /// <summary>
        /// Get a new database connection.
        /// </summary>
        public async Task<SqliteConnection> GetConnectionAsync(CancellationToken ct = default)
        {
            if (!_initialized)
                await InitializeAsync(ct);

            var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            return conn;
        }

        #region Symbol Operations

        /// <summary>
        /// Insert or update a symbol.
        /// </summary>
        public async Task<long> UpsertSymbolAsync(CodeSymbol symbol, CancellationToken ct = default)
        {
            await using var conn = await GetConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();

            cmd.CommandText = @"
                INSERT INTO symbols (name, full_name, kind, file_path, line_start, line_end, column_start, 
                                     signature, documentation, parent_id, visibility, is_static, return_type, last_indexed)
                VALUES (@name, @full_name, @kind, @file_path, @line_start, @line_end, @column_start,
                        @signature, @documentation, @parent_id, @visibility, @is_static, @return_type, @last_indexed)
                ON CONFLICT(id) DO UPDATE SET
                    name = @name, full_name = @full_name, signature = @signature, 
                    documentation = @documentation, last_indexed = @last_indexed
                RETURNING id;
            ";

            cmd.Parameters.AddWithValue("@name", symbol.Name);
            cmd.Parameters.AddWithValue("@full_name", (object?)symbol.FullName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@kind", symbol.Kind);
            cmd.Parameters.AddWithValue("@file_path", symbol.FilePath);
            cmd.Parameters.AddWithValue("@line_start", symbol.LineStart);
            cmd.Parameters.AddWithValue("@line_end", symbol.LineEnd);
            cmd.Parameters.AddWithValue("@column_start", symbol.ColumnStart);
            cmd.Parameters.AddWithValue("@signature", (object?)symbol.Signature ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@documentation", (object?)symbol.Documentation ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@parent_id", symbol.ParentId.HasValue ? symbol.ParentId.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@visibility", (object?)symbol.Visibility ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@is_static", symbol.IsStatic ? 1 : 0);
            cmd.Parameters.AddWithValue("@return_type", (object?)symbol.ReturnType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@last_indexed", DateTime.UtcNow.ToString("o"));

            var result = await cmd.ExecuteScalarAsync(ct);
            return result != null ? Convert.ToInt64(result) : 0;
        }

        /// <summary>
        /// Search for symbols by name pattern.
        /// </summary>
        public async Task<List<CodeSymbol>> SearchSymbolsAsync(string pattern, string? kind = null, 
            string? filePath = null, int limit = 50, CancellationToken ct = default)
        {
            await using var conn = await GetConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();

            var sql = @"
                SELECT id, name, full_name, kind, file_path, line_start, line_end, column_start,
                       signature, documentation, parent_id, visibility, is_static, return_type
                FROM symbols
                WHERE name LIKE @pattern
            ";

            if (!string.IsNullOrEmpty(kind))
                sql += " AND kind = @kind";
            if (!string.IsNullOrEmpty(filePath))
                sql += " AND file_path LIKE @file_path";
            
            sql += " ORDER BY name LIMIT @limit";

            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@pattern", $"%{pattern}%");
            if (!string.IsNullOrEmpty(kind))
                cmd.Parameters.AddWithValue("@kind", kind);
            if (!string.IsNullOrEmpty(filePath))
                cmd.Parameters.AddWithValue("@file_path", $"%{filePath}%");
            cmd.Parameters.AddWithValue("@limit", limit);

            var results = new List<CodeSymbol>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(ReadSymbol(reader));
            }
            return results;
        }

        /// <summary>
        /// Get all symbols in a file.
        /// </summary>
        public async Task<List<CodeSymbol>> GetSymbolsInFileAsync(string filePath, CancellationToken ct = default)
        {
            await using var conn = await GetConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();

            cmd.CommandText = @"
                SELECT id, name, full_name, kind, file_path, line_start, line_end, column_start,
                       signature, documentation, parent_id, visibility, is_static, return_type
                FROM symbols
                WHERE file_path = @file_path
                ORDER BY line_start
            ";
            cmd.Parameters.AddWithValue("@file_path", filePath);

            var results = new List<CodeSymbol>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(ReadSymbol(reader));
            }
            return results;
        }

        /// <summary>
        /// Get symbol by ID.
        /// </summary>
        public async Task<CodeSymbol?> GetSymbolAsync(long id, CancellationToken ct = default)
        {
            await using var conn = await GetConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();

            cmd.CommandText = @"
                SELECT id, name, full_name, kind, file_path, line_start, line_end, column_start,
                       signature, documentation, parent_id, visibility, is_static, return_type
                FROM symbols WHERE id = @id
            ";
            cmd.Parameters.AddWithValue("@id", id);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
                return ReadSymbol(reader);
            return null;
        }

        /// <summary>
        /// Delete all symbols for a file.
        /// </summary>
        public async Task<int> DeleteSymbolsForFileAsync(string filePath, CancellationToken ct = default)
        {
            await using var conn = await GetConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();

            cmd.CommandText = "DELETE FROM symbols WHERE file_path = @file_path";
            cmd.Parameters.AddWithValue("@file_path", filePath);

            return await cmd.ExecuteNonQueryAsync(ct);
        }

        private static CodeSymbol ReadSymbol(SqliteDataReader reader)
        {
            return new CodeSymbol
            {
                Id = reader.GetInt64(0),
                Name = reader.GetString(1),
                FullName = reader.IsDBNull(2) ? null : reader.GetString(2),
                Kind = reader.GetString(3),
                FilePath = reader.GetString(4),
                LineStart = reader.GetInt32(5),
                LineEnd = reader.GetInt32(6),
                ColumnStart = reader.GetInt32(7),
                Signature = reader.IsDBNull(8) ? null : reader.GetString(8),
                Documentation = reader.IsDBNull(9) ? null : reader.GetString(9),
                ParentId = reader.IsDBNull(10) ? null : reader.GetInt64(10),
                Visibility = reader.IsDBNull(11) ? null : reader.GetString(11),
                IsStatic = reader.GetInt32(12) == 1,
                ReturnType = reader.IsDBNull(13) ? null : reader.GetString(13)
            };
        }

        #endregion

        #region Reference Operations

        /// <summary>
        /// Add a reference to a symbol.
        /// </summary>
        public async Task AddReferenceAsync(CodeReference reference, CancellationToken ct = default)
        {
            await using var conn = await GetConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();

            cmd.CommandText = @"
                INSERT INTO ""references"" (symbol_id, file_path, line, column, context, reference_kind)
                VALUES (@symbol_id, @file_path, @line, @column, @context, @reference_kind)
            ";

            cmd.Parameters.AddWithValue("@symbol_id", reference.SymbolId);
            cmd.Parameters.AddWithValue("@file_path", reference.FilePath);
            cmd.Parameters.AddWithValue("@line", reference.Line);
            cmd.Parameters.AddWithValue("@column", reference.Column);
            cmd.Parameters.AddWithValue("@context", (object?)reference.Context ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@reference_kind", (object?)reference.ReferenceKind ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync(ct);
        }

        /// <summary>
        /// Find all references to a symbol.
        /// </summary>
        public async Task<List<CodeReference>> FindReferencesAsync(long symbolId, CancellationToken ct = default)
        {
            await using var conn = await GetConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();

            cmd.CommandText = @"
                SELECT id, symbol_id, file_path, line, column, context, reference_kind
                FROM ""references"" WHERE symbol_id = @symbol_id
                ORDER BY file_path, line
            ";
            cmd.Parameters.AddWithValue("@symbol_id", symbolId);

            var results = new List<CodeReference>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(new CodeReference
                {
                    Id = reader.GetInt64(0),
                    SymbolId = reader.GetInt64(1),
                    FilePath = reader.GetString(2),
                    Line = reader.GetInt32(3),
                    Column = reader.GetInt32(4),
                    Context = reader.IsDBNull(5) ? null : reader.GetString(5),
                    ReferenceKind = reader.IsDBNull(6) ? null : reader.GetString(6)
                });
            }
            return results;
        }

        #endregion

        #region Context Operations

        /// <summary>
        /// Store a context entry.
        /// </summary>
        public async Task<long> StoreContextAsync(string key, string value, string? category = null,
            string? projectPath = null, string? sessionId = null, int? expiresInDays = null, CancellationToken ct = default)
        {
            await using var conn = await GetConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();

            string? expiresAt = expiresInDays.HasValue
                ? DateTime.UtcNow.AddDays(expiresInDays.Value).ToString("o")
                : null;

            cmd.CommandText = @"
                INSERT INTO context (project_path, key, value, category, session_id, expires_at)
                VALUES (@project_path, @key, @value, @category, @session_id, @expires_at)
                RETURNING id
            ";

            cmd.Parameters.AddWithValue("@project_path", (object?)projectPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@value", value);
            cmd.Parameters.AddWithValue("@category", (object?)category ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@session_id", (object?)sessionId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@expires_at", (object?)expiresAt ?? DBNull.Value);

            var result = await cmd.ExecuteScalarAsync(ct);
            return result != null ? Convert.ToInt64(result) : 0;
        }

        /// <summary>
        /// Get context entries by key pattern.
        /// </summary>
        public async Task<List<ContextEntry>> GetContextAsync(string? keyPattern = null, string? category = null,
            string? projectPath = null, int limit = 50, CancellationToken ct = default)
        {
            await using var conn = await GetConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();

            var sql = @"
                SELECT id, project_path, key, value, category, created_at, expires_at, session_id
                FROM context
                WHERE (expires_at IS NULL OR expires_at > datetime('now'))
            ";

            if (!string.IsNullOrEmpty(keyPattern))
                sql += " AND key LIKE @key";
            if (!string.IsNullOrEmpty(category))
                sql += " AND category = @category";
            if (!string.IsNullOrEmpty(projectPath))
                sql += " AND project_path = @project_path";

            sql += " ORDER BY created_at DESC LIMIT @limit";

            cmd.CommandText = sql;
            if (!string.IsNullOrEmpty(keyPattern))
                cmd.Parameters.AddWithValue("@key", $"%{keyPattern}%");
            if (!string.IsNullOrEmpty(category))
                cmd.Parameters.AddWithValue("@category", category);
            if (!string.IsNullOrEmpty(projectPath))
                cmd.Parameters.AddWithValue("@project_path", projectPath);
            cmd.Parameters.AddWithValue("@limit", limit);

            var results = new List<ContextEntry>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(new ContextEntry
                {
                    Id = reader.GetInt64(0),
                    ProjectPath = reader.IsDBNull(1) ? null : reader.GetString(1),
                    Key = reader.GetString(2),
                    Value = reader.GetString(3),
                    Category = reader.IsDBNull(4) ? null : reader.GetString(4),
                    CreatedAt = reader.IsDBNull(5) ? null : reader.GetString(5),
                    ExpiresAt = reader.IsDBNull(6) ? null : reader.GetString(6),
                    SessionId = reader.IsDBNull(7) ? null : reader.GetString(7)
                });
            }
            return results;
        }

        /// <summary>
        /// Delete expired context entries.
        /// </summary>
        public async Task<int> CleanupExpiredContextAsync(CancellationToken ct = default)
        {
            await using var conn = await GetConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();

            cmd.CommandText = "DELETE FROM context WHERE expires_at IS NOT NULL AND expires_at < datetime('now')";
            return await cmd.ExecuteNonQueryAsync(ct);
        }

        #endregion

        #region File Operations

        /// <summary>
        /// Update file metadata.
        /// </summary>
        public async Task UpsertFileAsync(string path, string hash, long size, int symbolCount, CancellationToken ct = default)
        {
            await using var conn = await GetConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();

            cmd.CommandText = @"
                INSERT INTO files (path, hash, size, last_indexed, symbol_count)
                VALUES (@path, @hash, @size, @last_indexed, @symbol_count)
                ON CONFLICT(path) DO UPDATE SET
                    hash = @hash, size = @size, last_indexed = @last_indexed, symbol_count = @symbol_count
            ";

            cmd.Parameters.AddWithValue("@path", path);
            cmd.Parameters.AddWithValue("@hash", hash);
            cmd.Parameters.AddWithValue("@size", size);
            cmd.Parameters.AddWithValue("@last_indexed", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("@symbol_count", symbolCount);

            await cmd.ExecuteNonQueryAsync(ct);
        }

        /// <summary>
        /// Get file metadata.
        /// </summary>
        public async Task<FileMetadata?> GetFileMetadataAsync(string path, CancellationToken ct = default)
        {
            await using var conn = await GetConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();

            cmd.CommandText = "SELECT id, path, hash, size, last_indexed, symbol_count FROM files WHERE path = @path";
            cmd.Parameters.AddWithValue("@path", path);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                return new FileMetadata
                {
                    Id = reader.GetInt64(0),
                    Path = reader.GetString(1),
                    Hash = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Size = reader.GetInt64(3),
                    LastIndexed = reader.IsDBNull(4) ? null : reader.GetString(4),
                    SymbolCount = reader.GetInt32(5)
                };
            }
            return null;
        }

        #endregion

        #region Statistics

        /// <summary>
        /// Get index statistics.
        /// </summary>
        public async Task<IndexStats> GetStatsAsync(CancellationToken ct = default)
        {
            await using var conn = await GetConnectionAsync(ct);

            var stats = new IndexStats();

            // Count symbols by kind
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT kind, COUNT(*) FROM symbols GROUP BY kind";
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    stats.SymbolsByKind[reader.GetString(0)] = reader.GetInt64(1);
                    stats.TotalSymbols += reader.GetInt64(1);
                }
            }

            // Count files
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM files";
                stats.TotalFiles = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
            }

            // Count references
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT COUNT(*) FROM ""references""";
                stats.TotalReferences = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
            }

            // Count context entries
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM context WHERE expires_at IS NULL OR expires_at > datetime('now')";
                stats.TotalContextEntries = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
            }

            // Database size
            var dbPath = SqliteConfig.Instance.GetEffectiveDatabasePath();
            if (File.Exists(dbPath))
                stats.DatabaseSizeBytes = new FileInfo(dbPath).Length;

            return stats;
        }

        /// <summary>
        /// Clear all indexed data.
        /// </summary>
        public async Task<int> ClearAllAsync(CancellationToken ct = default)
        {
            await using var conn = await GetConnectionAsync(ct);
            int total = 0;

            // Note: "references" needs quotes as it's a reserved word
            foreach (var table in new[] { @"""references""", "symbols", "files", "context" })
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"DELETE FROM {table}";
                total += await cmd.ExecuteNonQueryAsync(ct);
            }

            return total;
        }

        #endregion
    }

    #region Models

    public class CodeSymbol
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string? FullName { get; set; }
        public string Kind { get; set; } = "";  // class, method, property, field, interface, enum
        public string FilePath { get; set; } = "";
        public int LineStart { get; set; }
        public int LineEnd { get; set; }
        public int ColumnStart { get; set; }
        public string? Signature { get; set; }
        public string? Documentation { get; set; }
        public long? ParentId { get; set; }
        public string? Visibility { get; set; }
        public bool IsStatic { get; set; }
        public string? ReturnType { get; set; }
    }

    public class CodeReference
    {
        public long Id { get; set; }
        public long SymbolId { get; set; }
        public string FilePath { get; set; } = "";
        public int Line { get; set; }
        public int Column { get; set; }
        public string? Context { get; set; }
        public string? ReferenceKind { get; set; }  // call, read, write, type, inherit
    }

    public class ContextEntry
    {
        public long Id { get; set; }
        public string? ProjectPath { get; set; }
        public string Key { get; set; } = "";
        public string Value { get; set; } = "";
        public string? Category { get; set; }
        public string? CreatedAt { get; set; }
        public string? ExpiresAt { get; set; }
        public string? SessionId { get; set; }
    }

    public class FileMetadata
    {
        public long Id { get; set; }
        public string Path { get; set; } = "";
        public string? Hash { get; set; }
        public long Size { get; set; }
        public string? LastIndexed { get; set; }
        public int SymbolCount { get; set; }
    }

    public class IndexStats
    {
        public long TotalSymbols { get; set; }
        public long TotalFiles { get; set; }
        public long TotalReferences { get; set; }
        public long TotalContextEntries { get; set; }
        public long DatabaseSizeBytes { get; set; }
        public Dictionary<string, long> SymbolsByKind { get; set; } = new();
    }

    #endregion
}
