using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

            // Enable WAL mode for better performance with concurrent reads/writes
            await using (var walCmd = conn.CreateCommand())
            {
                walCmd.CommandText = "PRAGMA journal_mode=WAL;";
                await walCmd.ExecuteNonQueryAsync(ct);
                AgentLogger.LogDebug("SQLite WAL mode enabled");
            }

            // Set busy timeout to wait up to 5 seconds for locks instead of failing immediately
            await using (var busyCmd = conn.CreateCommand())
            {
                busyCmd.CommandText = "PRAGMA busy_timeout=5000;";
                await busyCmd.ExecuteNonQueryAsync(ct);
            }

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

                -- Web agent sessions (lightweight metadata, messages stored separately)
                CREATE TABLE IF NOT EXISTS sessions (
                    session_id TEXT PRIMARY KEY,
                    created_at TEXT DEFAULT (datetime('now')),
                    last_activity_at TEXT DEFAULT (datetime('now')),
                    system_prompt TEXT,
                    model_id TEXT,
                    agent_role TEXT DEFAULT 'main',
                    title TEXT,                    -- Auto-generated or user-set session title
                    work_directory TEXT,
                    is_active INTEGER DEFAULT 0,   -- 1 if session is currently processing
                    metadata_json TEXT
                );

                -- Messages table for all LLM interactions (supports sub-agent hierarchy)
                CREATE TABLE IF NOT EXISTS messages (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    session_id TEXT NOT NULL,
                    parent_message_id INTEGER,     -- Self-referential FK for sub-agent hierarchy
                    
                    -- Timing
                    started_at TEXT NOT NULL,
                    completed_at TEXT,
                    duration_ms INTEGER,
                    
                    -- Agent Info
                    agent_role TEXT,               -- 'main', 'planner', 'coder', 'tester', 'reviewer', etc.
                    agent_depth INTEGER DEFAULT 0, -- 0=main, 1=sub-agent, 2=sub-sub-agent
                    model_id TEXT,
                    system_prompt_id TEXT,         -- Reference to role's system prompt
                    
                    -- Message type and direction
                    message_type TEXT NOT NULL,    -- 'user', 'assistant', 'tool_call', 'tool_result', 'delegation', 'system'
                    
                    -- Request (for user/delegation messages)
                    request_content TEXT,          -- User prompt or delegation task
                    context_mode TEXT,             -- 'full', 'summary', 'selective'
                    context_token_count INTEGER,
                    
                    -- Response (for assistant messages)
                    response_content TEXT,         -- Full LLM response
                    response_summary TEXT,         -- Condensed summary for parent (sub-agents)
                    
                    -- Tool calls
                    tool_name TEXT,                -- Tool name if this is a tool call/result
                    tool_args_json TEXT,           -- Tool arguments as JSON
                    tool_result_json TEXT,         -- Tool result as JSON
                    
                    -- Files tracking
                    files_modified_json TEXT,      -- JSON array of files touched
                    files_created_json TEXT,       -- JSON array of files created
                    
                    -- Token metrics
                    prompt_tokens INTEGER,
                    completion_tokens INTEGER,
                    total_tokens INTEGER,
                    
                    -- Iteration tracking (for agent loops)
                    iteration_number INTEGER DEFAULT 0,
                    
                    -- Bailout tracking
                    max_iterations INTEGER,
                    max_duration_ms INTEGER,
                    bailout_reason TEXT,           -- 'timeout', 'max_iterations', 'error', 'user_cancelled'
                    
                    -- Status
                    status TEXT DEFAULT 'pending', -- 'pending', 'running', 'completed', 'failed', 'cancelled', 'timeout'
                    error_message TEXT,
                    
                    -- Summarization tracking
                    is_summarized INTEGER DEFAULT 0, -- 1 if this message was included in a summary
                    summary_id INTEGER,              -- Reference to the summary message that replaced this
                    
                    -- Metadata
                    metadata_json TEXT,
                    
                    FOREIGN KEY (parent_message_id) REFERENCES messages(id),
                    FOREIGN KEY (summary_id) REFERENCES messages(id),
                    FOREIGN KEY (session_id) REFERENCES sessions(session_id)
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
                CREATE INDEX IF NOT EXISTS idx_sessions_last_activity ON sessions(last_activity_at);
                CREATE INDEX IF NOT EXISTS idx_messages_session ON messages(session_id);
                CREATE INDEX IF NOT EXISTS idx_messages_parent ON messages(parent_message_id);
                CREATE INDEX IF NOT EXISTS idx_messages_role ON messages(agent_role);
                CREATE INDEX IF NOT EXISTS idx_messages_depth ON messages(agent_depth);
                CREATE INDEX IF NOT EXISTS idx_messages_started ON messages(started_at);
                CREATE INDEX IF NOT EXISTS idx_messages_status ON messages(status);
                CREATE INDEX IF NOT EXISTS idx_messages_type ON messages(message_type);
            ";

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = schema;
            await cmd.ExecuteNonQueryAsync(ct);

            // Run migrations to add columns that may be missing in existing databases
            await RunMigrationsAsync(conn, ct);

            // Create indexes on migrated columns (after migrations ensure columns exist)
            await using var indexCmd = conn.CreateCommand();
            indexCmd.CommandText = @"
                CREATE INDEX IF NOT EXISTS idx_messages_summarized ON messages(is_summarized);
                CREATE INDEX IF NOT EXISTS idx_sessions_agent_id ON sessions(agent_id);
                CREATE INDEX IF NOT EXISTS idx_messages_agent_id ON messages(agent_id);
            ";
            await indexCmd.ExecuteNonQueryAsync(ct);

            // Create FTS5 virtual table for full-text search on messages
            await using var ftsCmd = conn.CreateCommand();
            ftsCmd.CommandText = @"
                CREATE VIRTUAL TABLE IF NOT EXISTS messages_fts USING fts5(
                    session_id UNINDEXED,
                    message_type UNINDEXED,
                    content,
                    tool_name UNINDEXED,
                    started_at UNINDEXED,
                    content=''
                );
            ";
            await ftsCmd.ExecuteNonQueryAsync(ct);

            _initialized = true;
            AgentLogger.LogInfo("SQLite database initialized at {Path}", dbPath);
        }

        /// <summary>
        /// Run database migrations to add missing columns to existing tables.
        /// This allows existing databases to be upgraded without losing data.
        /// </summary>
        private async Task RunMigrationsAsync(SqliteConnection conn, CancellationToken ct)
        {
            // Get existing columns in messages table
            var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var pragmaCmd = conn.CreateCommand())
            {
                pragmaCmd.CommandText = "PRAGMA table_info(messages);";
                await using var reader = await pragmaCmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    existingColumns.Add(reader.GetString(1)); // column name is at index 1
                }
            }

            // Migration: Add is_summarized column if missing
            if (!existingColumns.Contains("is_summarized"))
            {
                await using var alterCmd = conn.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE messages ADD COLUMN is_summarized INTEGER DEFAULT 0;";
                await alterCmd.ExecuteNonQueryAsync(ct);
                AgentLogger.LogInfo("Migration: Added is_summarized column to messages table");
            }

            // Migration: Add summary_id column if missing
            if (!existingColumns.Contains("summary_id"))
            {
                await using var alterCmd = conn.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE messages ADD COLUMN summary_id INTEGER;";
                await alterCmd.ExecuteNonQueryAsync(ct);
                AgentLogger.LogInfo("Migration: Added summary_id column to messages table");
            }

            // Get existing columns in sessions table
            existingColumns.Clear();
            await using (var pragmaCmd = conn.CreateCommand())
            {
                pragmaCmd.CommandText = "PRAGMA table_info(sessions);";
                await using var reader = await pragmaCmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    existingColumns.Add(reader.GetString(1));
                }
            }

            // Migration: Add is_active column if missing
            if (!existingColumns.Contains("is_active"))
            {
                await using var alterCmd = conn.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE sessions ADD COLUMN is_active INTEGER DEFAULT 0;";
                await alterCmd.ExecuteNonQueryAsync(ct);
                AgentLogger.LogInfo("Migration: Added is_active column to sessions table");
            }

            // Migration: Add agent_id column to sessions if missing
            if (!existingColumns.Contains("agent_id"))
            {
                await using var alterCmd = conn.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE sessions ADD COLUMN agent_id TEXT;";
                await alterCmd.ExecuteNonQueryAsync(ct);
                AgentLogger.LogInfo("Migration: Added agent_id column to sessions table");
            }

            // Migration: Add agent_id column to messages if missing
            {
                var msgColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                await using (var pragmaCmd = conn.CreateCommand())
                {
                    pragmaCmd.CommandText = "PRAGMA table_info(messages);";
                    await using var msgReader = await pragmaCmd.ExecuteReaderAsync(ct);
                    while (await msgReader.ReadAsync(ct))
                        msgColumns.Add(msgReader.GetString(1));
                }
                if (!msgColumns.Contains("agent_id"))
                {
                    await using var alterCmd = conn.CreateCommand();
                    alterCmd.CommandText = "ALTER TABLE messages ADD COLUMN agent_id TEXT;";
                    await alterCmd.ExecuteNonQueryAsync(ct);
                    AgentLogger.LogInfo("Migration: Added agent_id column to messages table");
                }
            }
        }

        /// <summary>
        /// Get a new database connection.
        /// </summary>
        public async Task<SqliteConnection> GetConnectionAsync(CancellationToken ct = default)
        {
            if (!_initialized)
                await InitializeAsync(ct).ConfigureAwait(false);

            var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            // Set busy timeout per-connection (PRAGMA is connection-scoped)
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA busy_timeout=5000;";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

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
            await using var conn = await GetConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();

            cmd.CommandText = "DELETE FROM symbols WHERE file_path = @file_path";
            cmd.Parameters.AddWithValue("@file_path", filePath);

            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Batch index a file: delete old symbols, insert new ones, and update file metadata in a single transaction.
        /// Much more efficient than individual UpsertSymbolAsync calls.
        /// </summary>
        public async Task IndexFileBatchAsync(string filePath, string hash, long fileSize,
            List<CodeSymbol> symbols, CancellationToken ct = default)
        {
            await using var conn = await GetConnectionAsync(ct).ConfigureAwait(false);
            await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

            try
            {
                // Delete existing symbols for this file
                await using (var delCmd = conn.CreateCommand())
                {
                    delCmd.CommandText = "DELETE FROM symbols WHERE file_path = @file_path";
                    delCmd.Parameters.AddWithValue("@file_path", filePath);
                    await delCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                // Insert all symbols
                foreach (var symbol in symbols)
                {
                    ct.ThrowIfCancellationRequested();
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        INSERT INTO symbols (name, full_name, kind, file_path, line_start, line_end, column_start, 
                                             signature, documentation, parent_id, visibility, is_static, return_type, last_indexed)
                        VALUES (@name, @full_name, @kind, @file_path, @line_start, @line_end, @column_start,
                                @signature, @documentation, @parent_id, @visibility, @is_static, @return_type, @last_indexed)
                    ";
                    cmd.Parameters.AddWithValue("@name", symbol.Name);
                    cmd.Parameters.AddWithValue("@full_name", (object?)symbol.FullName ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@kind", symbol.Kind);
                    cmd.Parameters.AddWithValue("@file_path", filePath);
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
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                // Upsert file metadata
                await using (var fileCmd = conn.CreateCommand())
                {
                    fileCmd.CommandText = @"
                        INSERT INTO files (path, hash, size, last_indexed, symbol_count)
                        VALUES (@path, @hash, @size, @last_indexed, @symbol_count)
                        ON CONFLICT(path) DO UPDATE SET
                            hash = @hash, size = @size, last_indexed = @last_indexed, symbol_count = @symbol_count
                    ";
                    fileCmd.Parameters.AddWithValue("@path", filePath);
                    fileCmd.Parameters.AddWithValue("@hash", hash);
                    fileCmd.Parameters.AddWithValue("@size", fileSize);
                    fileCmd.Parameters.AddWithValue("@last_indexed", DateTime.UtcNow.ToString("o"));
                    fileCmd.Parameters.AddWithValue("@symbol_count", symbols.Count);
                    await fileCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                throw;
            }
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

            // Get last indexed timestamp
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT MAX(indexed_at) FROM files";
                var result = await cmd.ExecuteScalarAsync(ct);
                if (result != null && result != DBNull.Value)
                {
                    stats.LastIndexedAt = DateTime.Parse(result.ToString()!);
                }
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

        #region Session Operations

        /// <summary>
        /// Save or update a session to the database.
        /// </summary>
        public async Task SaveSessionAsync(SessionData session, CancellationToken ct = default)
        {
            await using var conn = await GetConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();

            cmd.CommandText = @"
                INSERT INTO sessions (session_id, agent_id, system_prompt, model_id, agent_role, title, 
                                      work_directory, created_at, last_activity_at, metadata_json)
                VALUES (@session_id, @agent_id, @system_prompt, @model_id, @agent_role, @title,
                        @work_directory, @created_at, @last_activity_at, @metadata_json)
                ON CONFLICT(session_id) DO UPDATE SET
                    agent_id = @agent_id,
                    system_prompt = @system_prompt,
                    model_id = @model_id,
                    agent_role = @agent_role,
                    title = @title,
                    work_directory = @work_directory,
                    last_activity_at = @last_activity_at,
                    metadata_json = @metadata_json
            ";

            cmd.Parameters.AddWithValue("@session_id", session.SessionId);
            cmd.Parameters.AddWithValue("@agent_id", (object?)session.AgentId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@system_prompt", (object?)session.SystemPrompt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@model_id", (object?)session.ModelId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@agent_role", (object?)session.AgentRole ?? "main");
            cmd.Parameters.AddWithValue("@title", (object?)session.Title ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@work_directory", (object?)session.WorkDirectory ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@created_at", session.CreatedAt.ToString("o"));
            cmd.Parameters.AddWithValue("@last_activity_at", session.LastActivityAt.ToString("o"));
            cmd.Parameters.AddWithValue("@metadata_json", (object?)session.MetadataJson ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync(ct);
            AgentLogger.LogDebug("Session {SessionId} saved to database", session.SessionId);
        }

        /// <summary>
        /// Load a session from the database.
        /// </summary>
        public async Task<SessionData?> LoadSessionAsync(string sessionId, CancellationToken ct = default)
        {
            await using var conn = await GetConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();

            cmd.CommandText = @"
                SELECT session_id, system_prompt, model_id, agent_role, title,
                       work_directory, created_at, last_activity_at, metadata_json, agent_id
                FROM sessions
                WHERE session_id = @session_id
            ";
            cmd.Parameters.AddWithValue("@session_id", sessionId);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                return ReadSession(reader);
            }
            return null;
        }

        /// <summary>
        /// Get list of recent sessions with message counts.
        /// </summary>
        public async Task<List<SessionData>> GetRecentSessionsAsync(int limit = 10, CancellationToken ct = default)
        {
            await using var conn = await GetConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();

            cmd.CommandText = @"
                SELECT s.session_id, s.system_prompt, s.model_id, s.agent_role, s.title,
                       s.work_directory, s.created_at, s.last_activity_at, s.metadata_json, s.agent_id,
                       (SELECT COUNT(*) FROM messages m WHERE m.session_id = s.session_id) as message_count
                FROM sessions s
                ORDER BY s.last_activity_at DESC
                LIMIT @limit
            ";
            cmd.Parameters.AddWithValue("@limit", limit);

            var results = new List<SessionData>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var session = ReadSession(reader);
                session.MessageCount = reader.IsDBNull(10) ? 0 : reader.GetInt32(10);
                results.Add(session);
            }
            return results;
        }

        /// <summary>
        /// Get sessions for a specific agent ID, ordered by last activity.
        /// </summary>
        public async Task<List<SessionData>> GetSessionsByAgentIdAsync(string agentId, CancellationToken ct = default)
        {
            await using var conn = await GetConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();

            cmd.CommandText = @"
                SELECT s.session_id, s.system_prompt, s.model_id, s.agent_role, s.title,
                       s.work_directory, s.created_at, s.last_activity_at, s.metadata_json, s.agent_id,
                       (SELECT COUNT(*) FROM messages m WHERE m.session_id = s.session_id) as message_count
                FROM sessions s
                WHERE s.agent_id = @agent_id
                ORDER BY s.created_at ASC
            ";
            cmd.Parameters.AddWithValue("@agent_id", agentId);

            var results = new List<SessionData>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var session = ReadSession(reader);
                session.MessageCount = reader.IsDBNull(10) ? 0 : reader.GetInt32(10);
                results.Add(session);
            }
            return results;
        }

        /// <summary>
        /// Delete a session and its messages from the database.
        /// </summary>
        public async Task<bool> DeleteSessionAsync(string sessionId, CancellationToken ct = default)
        {
            await using var conn = await GetConnectionAsync(ct);
            
            // Delete messages first (FK constraint)
            await using (var msgCmd = conn.CreateCommand())
            {
                msgCmd.CommandText = "DELETE FROM messages WHERE session_id = @session_id";
                msgCmd.Parameters.AddWithValue("@session_id", sessionId);
                await msgCmd.ExecuteNonQueryAsync(ct);
            }
            
            // Delete session
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM sessions WHERE session_id = @session_id";
            cmd.Parameters.AddWithValue("@session_id", sessionId);

            var deleted = await cmd.ExecuteNonQueryAsync(ct);
            return deleted > 0;
        }

        /// <summary>
        /// Clean up old sessions and their messages (older than specified days).
        /// </summary>
        public async Task<int> CleanupOldSessionsAsync(int olderThanDays = 7, CancellationToken ct = default)
        {
            await using var conn = await GetConnectionAsync(ct);
            
            // Get old session IDs
            var oldSessionIds = new List<string>();
            await using (var selectCmd = conn.CreateCommand())
            {
                selectCmd.CommandText = @"
                    SELECT session_id FROM sessions 
                    WHERE last_activity_at < datetime('now', @days_ago)
                ";
                selectCmd.Parameters.AddWithValue("@days_ago", $"-{olderThanDays} days");
                await using var reader = await selectCmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    oldSessionIds.Add(reader.GetString(0));
                }
            }

            if (oldSessionIds.Count == 0) return 0;

            // Delete messages for old sessions
            foreach (var sid in oldSessionIds)
            {
                await using var msgCmd = conn.CreateCommand();
                msgCmd.CommandText = "DELETE FROM messages WHERE session_id = @session_id";
                msgCmd.Parameters.AddWithValue("@session_id", sid);
                await msgCmd.ExecuteNonQueryAsync(ct);
            }

            // Delete old sessions
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                DELETE FROM sessions 
                WHERE last_activity_at < datetime('now', @days_ago)
            ";
            cmd.Parameters.AddWithValue("@days_ago", $"-{olderThanDays} days");

            return await cmd.ExecuteNonQueryAsync(ct);
        }

        /// <summary>
        /// Update session's last activity timestamp.
        /// </summary>
        public async Task UpdateSessionActivityAsync(string sessionId, CancellationToken ct = default)
        {
            await using var conn = await GetConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();

            cmd.CommandText = @"
                UPDATE sessions 
                SET last_activity_at = datetime('now')
                WHERE session_id = @session_id
            ";
            cmd.Parameters.AddWithValue("@session_id", sessionId);

            await cmd.ExecuteNonQueryAsync(ct);
        }

        /// <summary>
        /// Set session active/inactive status.
        /// </summary>
        public async Task SetSessionActiveAsync(string sessionId, bool isActive, CancellationToken ct = default)
        {
            await using var conn = await GetConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();

            cmd.CommandText = @"
                UPDATE sessions 
                SET is_active = @is_active, last_activity_at = datetime('now')
                WHERE session_id = @session_id
            ";
            cmd.Parameters.AddWithValue("@session_id", sessionId);
            cmd.Parameters.AddWithValue("@is_active", isActive ? 1 : 0);

            await cmd.ExecuteNonQueryAsync(ct);
        }

        /// <summary>
        /// Delete all messages for a session (used before re-syncing).
        /// </summary>
        public async Task DeleteSessionMessagesAsync(string sessionId, CancellationToken ct = default)
        {
            await using var conn = await GetConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();

            cmd.CommandText = "DELETE FROM messages WHERE session_id = @session_id";
            cmd.Parameters.AddWithValue("@session_id", sessionId);

            await cmd.ExecuteNonQueryAsync(ct);
        }

        private static SessionData ReadSession(SqliteDataReader reader)
        {
            return new SessionData
            {
                SessionId = reader.GetString(0),
                SystemPrompt = reader.IsDBNull(1) ? null : reader.GetString(1),
                ModelId = reader.IsDBNull(2) ? null : reader.GetString(2),
                AgentRole = reader.IsDBNull(3) ? "main" : reader.GetString(3),
                Title = reader.IsDBNull(4) ? null : reader.GetString(4),
                WorkDirectory = reader.IsDBNull(5) ? null : reader.GetString(5),
                CreatedAt = reader.IsDBNull(6) ? DateTime.Now : DateTime.Parse(reader.GetString(6)),
                LastActivityAt = reader.IsDBNull(7) ? DateTime.Now : DateTime.Parse(reader.GetString(7)),
                MetadataJson = reader.IsDBNull(8) ? null : reader.GetString(8),
                AgentId = reader.FieldCount > 9 && !reader.IsDBNull(9) ? reader.GetString(9) : null
            };
        }

        #endregion

        #region Message Operations

        /// <summary>
        /// Start a new message record (returns message ID).
        /// </summary>
        public async Task<long> StartMessageAsync(MessageRecord message, CancellationToken ct = default)
        {
            await using var conn = await GetConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();

            cmd.CommandText = @"
                INSERT INTO messages (
                    session_id, agent_id, parent_message_id, started_at, agent_role, agent_depth, model_id,
                    system_prompt_id, message_type, request_content, response_content, context_mode, context_token_count,
                    tool_name, tool_args_json, tool_result_json,
                    prompt_tokens, completion_tokens, total_tokens,
                    max_iterations, max_duration_ms, status
                )
                VALUES (
                    @session_id, @agent_id, @parent_message_id, @started_at, @agent_role, @agent_depth, @model_id,
                    @system_prompt_id, @message_type, @request_content, @response_content, @context_mode, @context_token_count,
                    @tool_name, @tool_args_json, @tool_result_json,
                    @prompt_tokens, @completion_tokens, @total_tokens,
                    @max_iterations, @max_duration_ms, @status
                )
                RETURNING id
            ";

            cmd.Parameters.AddWithValue("@session_id", message.SessionId);
            cmd.Parameters.AddWithValue("@agent_id", (object?)message.AgentId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@parent_message_id", message.ParentMessageId.HasValue ? message.ParentMessageId.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@started_at", message.StartedAt.ToString("o"));
            cmd.Parameters.AddWithValue("@agent_role", (object?)message.AgentRole ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@agent_depth", message.AgentDepth);
            cmd.Parameters.AddWithValue("@model_id", (object?)message.ModelId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@system_prompt_id", (object?)message.SystemPromptId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@message_type", message.MessageType);
            cmd.Parameters.AddWithValue("@request_content", (object?)message.RequestContent ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@response_content", (object?)message.ResponseContent ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@context_mode", (object?)message.ContextMode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@context_token_count", message.ContextTokenCount.HasValue ? message.ContextTokenCount.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@tool_name", (object?)message.ToolName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@tool_args_json", (object?)message.ToolArgsJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@tool_result_json", (object?)message.ToolResultJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@prompt_tokens", message.PromptTokens.HasValue ? message.PromptTokens.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@completion_tokens", message.CompletionTokens.HasValue ? message.CompletionTokens.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@total_tokens", message.TotalTokens.HasValue ? message.TotalTokens.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@max_iterations", message.MaxIterations.HasValue ? message.MaxIterations.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@max_duration_ms", message.MaxDurationMs.HasValue ? message.MaxDurationMs.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@status", message.Status ?? "running");

            var result = await cmd.ExecuteScalarAsync(ct);
            var messageId = result != null ? Convert.ToInt64(result) : 0;
            
            // Index request content in FTS
            if (!string.IsNullOrWhiteSpace(message.RequestContent))
            {
                await IndexMessageInFtsAsync(conn, messageId, message.SessionId, message.MessageType, 
                    message.RequestContent, message.ToolName, message.StartedAt, ct);
            }
            
            // Update session activity
            await UpdateSessionActivityAsync(message.SessionId, ct);
            
            return messageId;
        }

        /// <summary>
        /// Complete a message with response data.
        /// </summary>
        public async Task CompleteMessageAsync(long messageId, MessageCompleteInfo info, CancellationToken ct = default)
        {
            await using var conn = await GetConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();

            cmd.CommandText = @"
                UPDATE messages SET
                    completed_at = @completed_at,
                    duration_ms = @duration_ms,
                    response_content = @response_content,
                    response_summary = @response_summary,
                    tool_name = @tool_name,
                    tool_args_json = @tool_args_json,
                    tool_result_json = @tool_result_json,
                    files_modified_json = @files_modified_json,
                    files_created_json = @files_created_json,
                    prompt_tokens = @prompt_tokens,
                    completion_tokens = @completion_tokens,
                    total_tokens = @total_tokens,
                    iteration_number = @iteration_number,
                    status = 'completed'
                WHERE id = @id
            ";

            cmd.Parameters.AddWithValue("@id", messageId);
            cmd.Parameters.AddWithValue("@completed_at", info.CompletedAt.ToString("o"));
            cmd.Parameters.AddWithValue("@duration_ms", info.DurationMs);
            cmd.Parameters.AddWithValue("@response_content", (object?)info.ResponseContent ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@response_summary", (object?)info.ResponseSummary ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@tool_name", (object?)info.ToolName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@tool_args_json", (object?)info.ToolArgsJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@tool_result_json", (object?)info.ToolResultJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@files_modified_json", (object?)info.FilesModifiedJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@files_created_json", (object?)info.FilesCreatedJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@prompt_tokens", info.PromptTokens.HasValue ? info.PromptTokens.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@completion_tokens", info.CompletionTokens.HasValue ? info.CompletionTokens.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@total_tokens", info.TotalTokens.HasValue ? info.TotalTokens.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@iteration_number", info.IterationNumber);

            await cmd.ExecuteNonQueryAsync(ct);

            // Update FTS index with response content
            if (!string.IsNullOrWhiteSpace(info.ResponseContent))
            {
                // Delete old entry and re-insert with combined content
                await UpdateMessageFtsAsync(conn, messageId, info.ResponseContent, info.ToolName, ct);
            }
        }
        public async Task FailMessageAsync(long messageId, string error, string? bailoutReason = null, CancellationToken ct = default)
        {
            await using var conn = await GetConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();

            cmd.CommandText = @"
                UPDATE messages SET
                    completed_at = datetime('now'),
                    status = @status,
                    error_message = @error_message,
                    bailout_reason = @bailout_reason
                WHERE id = @id
            ";

            cmd.Parameters.AddWithValue("@id", messageId);
            cmd.Parameters.AddWithValue("@status", bailoutReason != null ? "timeout" : "failed");
            cmd.Parameters.AddWithValue("@error_message", error);
            cmd.Parameters.AddWithValue("@bailout_reason", (object?)bailoutReason ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync(ct);
        }

        /// <summary>
        /// Record a summarization event: mark messages as summarized and create a summary message.
        /// Returns the ID of the new summary message.
        /// </summary>
        public async Task<long> RecordSummarizationAsync(
            string sessionId,
            string summaryContent,
            List<long> summarizedMessageIds,
            CancellationToken ct = default)
        {
            await using var conn = await GetConnectionAsync(ct);
            var transaction = conn.BeginTransaction();
            
            try
            {
                // 1. Insert the summary message
                await using var insertCmd = conn.CreateCommand();
                insertCmd.Transaction = transaction;
                insertCmd.CommandText = @"
                    INSERT INTO messages (
                        session_id, started_at, agent_role, agent_depth, model_id,
                        message_type, response_content, status
                    )
                    VALUES (
                        @session_id, @started_at, 'main', 0, @model_id,
                        'summary', @response_content, 'completed'
                    )
                    RETURNING id
                ";
                
                insertCmd.Parameters.AddWithValue("@session_id", sessionId);
                insertCmd.Parameters.AddWithValue("@started_at", DateTime.Now.ToString("o"));
                insertCmd.Parameters.AddWithValue("@model_id", (object?)AgentConfig.Config.Model ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue("@response_content", summaryContent);
                
                var summaryId = Convert.ToInt64(await insertCmd.ExecuteScalarAsync(ct));
                
                // 2. Mark all summarized messages
                if (summarizedMessageIds.Count > 0)
                {
                    await using var updateCmd = conn.CreateCommand();
                    updateCmd.Transaction = transaction;
                    
                    // Build parameterized IN clause
                    var idParams = string.Join(",", summarizedMessageIds.Select((_, i) => $"@id{i}"));
                    updateCmd.CommandText = $@"
                        UPDATE messages SET
                            is_summarized = 1,
                            summary_id = @summary_id
                        WHERE id IN ({idParams})
                    ";
                    
                    updateCmd.Parameters.AddWithValue("@summary_id", summaryId);
                    for (int i = 0; i < summarizedMessageIds.Count; i++)
                    {
                        updateCmd.Parameters.AddWithValue($"@id{i}", summarizedMessageIds[i]);
                    }
                    
                    await updateCmd.ExecuteNonQueryAsync(ct);
                }
                
                transaction.Commit();
                return summaryId;
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        /// <summary>
        /// Get messages for context reconstruction, respecting summarization.
        /// Returns only non-summarized messages, plus the latest summary if one exists.
        /// </summary>
        public async Task<List<MessageRecord>> GetActiveSessionMessagesAsync(string sessionId, CancellationToken ct = default)
        {
            await using var conn = await GetConnectionAsync(ct);
            
            // First, check if there's a summary message
            await using var summaryCmd = conn.CreateCommand();
            summaryCmd.CommandText = @"
                SELECT id, session_id, parent_message_id, started_at, completed_at, duration_ms,
                       agent_role, agent_depth, model_id, system_prompt_id, message_type,
                       request_content, context_mode, context_token_count, response_content, response_summary,
                       tool_name, tool_args_json, tool_result_json, files_modified_json, files_created_json,
                       prompt_tokens, completion_tokens, total_tokens, iteration_number,
                       max_iterations, max_duration_ms, bailout_reason, status, error_message, metadata_json,
                       is_summarized, summary_id
                FROM messages
                WHERE session_id = @session_id AND message_type = 'summary'
                ORDER BY started_at DESC
                LIMIT 1
            ";
            summaryCmd.Parameters.AddWithValue("@session_id", sessionId);
            
            MessageRecord? latestSummary = null;
            await using (var reader = await summaryCmd.ExecuteReaderAsync(ct))
            {
                if (await reader.ReadAsync(ct))
                {
                    latestSummary = ReadMessageWithSummaryFields(reader);
                }
            }
            
            var results = new List<MessageRecord>();
            
            if (latestSummary != null)
            {
                // Add the summary first
                results.Add(latestSummary);
                
                // Then get all non-summarized messages after the summary
                await using var afterCmd = conn.CreateCommand();
                afterCmd.CommandText = @"
                    SELECT id, session_id, parent_message_id, started_at, completed_at, duration_ms,
                           agent_role, agent_depth, model_id, system_prompt_id, message_type,
                           request_content, context_mode, context_token_count, response_content, response_summary,
                           tool_name, tool_args_json, tool_result_json, files_modified_json, files_created_json,
                           prompt_tokens, completion_tokens, total_tokens, iteration_number,
                           max_iterations, max_duration_ms, bailout_reason, status, error_message, metadata_json,
                           is_summarized, summary_id
                    FROM messages
                    WHERE session_id = @session_id 
                      AND is_summarized = 0
                      AND message_type != 'summary'
                      AND started_at > @summary_time
                    ORDER BY started_at ASC
                ";
                afterCmd.Parameters.AddWithValue("@session_id", sessionId);
                afterCmd.Parameters.AddWithValue("@summary_time", latestSummary.StartedAt.ToString("o"));
                
                await using var afterReader = await afterCmd.ExecuteReaderAsync(ct);
                while (await afterReader.ReadAsync(ct))
                {
                    results.Add(ReadMessageWithSummaryFields(afterReader));
                }
            }
            else
            {
                // No summary - get only non-summarized messages (in case summarization happened but summary record is missing)
                await using var allCmd = conn.CreateCommand();
                allCmd.CommandText = @"
                    SELECT id, session_id, parent_message_id, started_at, completed_at, duration_ms,
                           agent_role, agent_depth, model_id, system_prompt_id, message_type,
                           request_content, context_mode, context_token_count, response_content, response_summary,
                           tool_name, tool_args_json, tool_result_json, files_modified_json, files_created_json,
                           prompt_tokens, completion_tokens, total_tokens, iteration_number,
                           max_iterations, max_duration_ms, bailout_reason, status, error_message, metadata_json,
                           is_summarized, summary_id
                    FROM messages
                    WHERE session_id = @session_id
                      AND is_summarized = 0
                      AND message_type != 'summary'
                    ORDER BY started_at ASC
                ";
                allCmd.Parameters.AddWithValue("@session_id", sessionId);
                
                await using var allReader = await allCmd.ExecuteReaderAsync(ct);
                while (await allReader.ReadAsync(ct))
                {
                    results.Add(ReadMessageWithSummaryFields(allReader));
                }
            }
            
            return results;
        }

        /// <summary>
        /// Get IDs of all non-summarized messages in a session (for marking during summarization).
        /// </summary>
        public async Task<List<long>> GetNonSummarizedMessageIdsAsync(string sessionId, CancellationToken ct = default)
        {
            await using var conn = await GetConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();
            
            cmd.CommandText = @"
                SELECT id FROM messages
                WHERE session_id = @session_id 
                  AND is_summarized = 0
                  AND message_type NOT IN ('system', 'summary')
                ORDER BY started_at ASC
            ";
            cmd.Parameters.AddWithValue("@session_id", sessionId);
            
            var ids = new List<long>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                ids.Add(reader.GetInt64(0));
            }
            return ids;
        }

        /// <summary>
        /// Get all messages for a session, ordered by start time.
        /// </summary>
        public async Task<List<MessageRecord>> GetSessionMessagesAsync(string sessionId, CancellationToken ct = default)
        {
            await using var conn = await GetConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();

            cmd.CommandText = @"
                SELECT id, session_id, parent_message_id, started_at, completed_at, duration_ms,
                       agent_role, agent_depth, model_id, system_prompt_id, message_type,
                       request_content, context_mode, context_token_count, response_content, response_summary,
                       tool_name, tool_args_json, tool_result_json, files_modified_json, files_created_json,
                       prompt_tokens, completion_tokens, total_tokens, iteration_number,
                       max_iterations, max_duration_ms, bailout_reason, status, error_message, metadata_json
                FROM messages
                WHERE session_id = @session_id
                ORDER BY started_at ASC
            ";
            cmd.Parameters.AddWithValue("@session_id", sessionId);

            var results = new List<MessageRecord>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(ReadMessage(reader));
            }
            return results;
        }

        /// <summary>
        /// Get child messages of a parent message (for sub-agent hierarchy).
        /// </summary>
        public async Task<List<MessageRecord>> GetChildMessagesAsync(long parentMessageId, CancellationToken ct = default)
        {
            await using var conn = await GetConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();

            cmd.CommandText = @"
                SELECT id, session_id, parent_message_id, started_at, completed_at, duration_ms,
                       agent_role, agent_depth, model_id, system_prompt_id, message_type,
                       request_content, context_mode, context_token_count, response_content, response_summary,
                       tool_name, tool_args_json, tool_result_json, files_modified_json, files_created_json,
                       prompt_tokens, completion_tokens, total_tokens, iteration_number,
                       max_iterations, max_duration_ms, bailout_reason, status, error_message, metadata_json
                FROM messages
                WHERE parent_message_id = @parent_id
                ORDER BY started_at ASC
            ";
            cmd.Parameters.AddWithValue("@parent_id", parentMessageId);

            var results = new List<MessageRecord>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(ReadMessage(reader));
            }
            return results;
        }

        /// <summary>
        /// Get a single message by ID.
        /// </summary>
        public async Task<MessageRecord?> GetMessageAsync(long messageId, CancellationToken ct = default)
        {
            await using var conn = await GetConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();

            cmd.CommandText = @"
                SELECT id, session_id, parent_message_id, started_at, completed_at, duration_ms,
                       agent_role, agent_depth, model_id, system_prompt_id, message_type,
                       request_content, context_mode, context_token_count, response_content, response_summary,
                       tool_name, tool_args_json, tool_result_json, files_modified_json, files_created_json,
                       prompt_tokens, completion_tokens, total_tokens, iteration_number,
                       max_iterations, max_duration_ms, bailout_reason, status, error_message, metadata_json
                FROM messages
                WHERE id = @id
            ";
            cmd.Parameters.AddWithValue("@id", messageId);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                return ReadMessage(reader);
            }
            return null;
        }

        /// <summary>
        /// Get token usage statistics for a session.
        /// </summary>
        public async Task<SessionTokenStats> GetSessionTokenStatsAsync(string sessionId, CancellationToken ct = default)
        {
            await using var conn = await GetConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();

            cmd.CommandText = @"
                SELECT 
                    COUNT(*) as message_count,
                    SUM(COALESCE(prompt_tokens, 0)) as total_prompt_tokens,
                    SUM(COALESCE(completion_tokens, 0)) as total_completion_tokens,
                    SUM(COALESCE(total_tokens, 0)) as total_tokens,
                    SUM(COALESCE(duration_ms, 0)) as total_duration_ms,
                    MAX(agent_depth) as max_depth
                FROM messages
                WHERE session_id = @session_id
            ";
            cmd.Parameters.AddWithValue("@session_id", sessionId);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                return new SessionTokenStats
                {
                    MessageCount = reader.GetInt32(0),
                    TotalPromptTokens = reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
                    TotalCompletionTokens = reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                    TotalTokens = reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
                    TotalDurationMs = reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                    MaxDepth = reader.IsDBNull(5) ? 0 : reader.GetInt32(5)
                };
            }
            return new SessionTokenStats();
        }

        private static MessageRecord ReadMessage(SqliteDataReader reader)
        {
            return new MessageRecord
            {
                Id = reader.GetInt64(0),
                SessionId = reader.GetString(1),
                ParentMessageId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                StartedAt = DateTime.Parse(reader.GetString(3)),
                CompletedAt = reader.IsDBNull(4) ? null : DateTime.Parse(reader.GetString(4)),
                DurationMs = reader.IsDBNull(5) ? null : reader.GetInt64(5),
                AgentRole = reader.IsDBNull(6) ? null : reader.GetString(6),
                AgentDepth = reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                ModelId = reader.IsDBNull(8) ? null : reader.GetString(8),
                SystemPromptId = reader.IsDBNull(9) ? null : reader.GetString(9),
                MessageType = reader.GetString(10),
                RequestContent = reader.IsDBNull(11) ? null : reader.GetString(11),
                ContextMode = reader.IsDBNull(12) ? null : reader.GetString(12),
                ContextTokenCount = reader.IsDBNull(13) ? null : reader.GetInt32(13),
                ResponseContent = reader.IsDBNull(14) ? null : reader.GetString(14),
                ResponseSummary = reader.IsDBNull(15) ? null : reader.GetString(15),
                ToolName = reader.IsDBNull(16) ? null : reader.GetString(16),
                ToolArgsJson = reader.IsDBNull(17) ? null : reader.GetString(17),
                ToolResultJson = reader.IsDBNull(18) ? null : reader.GetString(18),
                FilesModifiedJson = reader.IsDBNull(19) ? null : reader.GetString(19),
                FilesCreatedJson = reader.IsDBNull(20) ? null : reader.GetString(20),
                PromptTokens = reader.IsDBNull(21) ? null : reader.GetInt32(21),
                CompletionTokens = reader.IsDBNull(22) ? null : reader.GetInt32(22),
                TotalTokens = reader.IsDBNull(23) ? null : reader.GetInt32(23),
                IterationNumber = reader.IsDBNull(24) ? 0 : reader.GetInt32(24),
                MaxIterations = reader.IsDBNull(25) ? null : reader.GetInt32(25),
                MaxDurationMs = reader.IsDBNull(26) ? null : reader.GetInt64(26),
                BailoutReason = reader.IsDBNull(27) ? null : reader.GetString(27),
                Status = reader.GetString(28),
                ErrorMessage = reader.IsDBNull(29) ? null : reader.GetString(29),
                MetadataJson = reader.IsDBNull(30) ? null : reader.GetString(30)
            };
        }

        /// <summary>
        /// Read a message record including summarization fields (columns 31-32).
        /// </summary>
        private static MessageRecord ReadMessageWithSummaryFields(SqliteDataReader reader)
        {
            var msg = new MessageRecord
            {
                Id = reader.GetInt64(0),
                SessionId = reader.GetString(1),
                ParentMessageId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                StartedAt = DateTime.Parse(reader.GetString(3)),
                CompletedAt = reader.IsDBNull(4) ? null : DateTime.Parse(reader.GetString(4)),
                DurationMs = reader.IsDBNull(5) ? null : reader.GetInt64(5),
                AgentRole = reader.IsDBNull(6) ? null : reader.GetString(6),
                AgentDepth = reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                ModelId = reader.IsDBNull(8) ? null : reader.GetString(8),
                SystemPromptId = reader.IsDBNull(9) ? null : reader.GetString(9),
                MessageType = reader.GetString(10),
                RequestContent = reader.IsDBNull(11) ? null : reader.GetString(11),
                ContextMode = reader.IsDBNull(12) ? null : reader.GetString(12),
                ContextTokenCount = reader.IsDBNull(13) ? null : reader.GetInt32(13),
                ResponseContent = reader.IsDBNull(14) ? null : reader.GetString(14),
                ResponseSummary = reader.IsDBNull(15) ? null : reader.GetString(15),
                ToolName = reader.IsDBNull(16) ? null : reader.GetString(16),
                ToolArgsJson = reader.IsDBNull(17) ? null : reader.GetString(17),
                ToolResultJson = reader.IsDBNull(18) ? null : reader.GetString(18),
                FilesModifiedJson = reader.IsDBNull(19) ? null : reader.GetString(19),
                FilesCreatedJson = reader.IsDBNull(20) ? null : reader.GetString(20),
                PromptTokens = reader.IsDBNull(21) ? null : reader.GetInt32(21),
                CompletionTokens = reader.IsDBNull(22) ? null : reader.GetInt32(22),
                TotalTokens = reader.IsDBNull(23) ? null : reader.GetInt32(23),
                IterationNumber = reader.IsDBNull(24) ? 0 : reader.GetInt32(24),
                MaxIterations = reader.IsDBNull(25) ? null : reader.GetInt32(25),
                MaxDurationMs = reader.IsDBNull(26) ? null : reader.GetInt64(26),
                BailoutReason = reader.IsDBNull(27) ? null : reader.GetString(27),
                Status = reader.GetString(28),
                ErrorMessage = reader.IsDBNull(29) ? null : reader.GetString(29),
                MetadataJson = reader.IsDBNull(30) ? null : reader.GetString(30),
                IsSummarized = reader.FieldCount > 31 && !reader.IsDBNull(31) && reader.GetInt32(31) == 1,
                SummaryId = reader.FieldCount > 32 && !reader.IsDBNull(32) ? reader.GetInt64(32) : null
            };
            return msg;
        }

        #endregion

        #region Memory Search (FTS)

        private async Task IndexMessageInFtsAsync(SqliteConnection conn, long messageId, string sessionId, 
            string messageType, string content, string? toolName, DateTime startedAt, CancellationToken ct)
        {
            try
            {
                await using var ftsCmd = conn.CreateCommand();
                ftsCmd.CommandText = @"
                    INSERT INTO messages_fts(rowid, session_id, message_type, content, tool_name, started_at)
                    VALUES (@id, @session_id, @message_type, @content, @tool_name, @started_at)
                ";
                ftsCmd.Parameters.AddWithValue("@id", messageId);
                ftsCmd.Parameters.AddWithValue("@session_id", sessionId);
                ftsCmd.Parameters.AddWithValue("@message_type", messageType);
                ftsCmd.Parameters.AddWithValue("@content", content);
                ftsCmd.Parameters.AddWithValue("@tool_name", (object?)toolName ?? DBNull.Value);
                ftsCmd.Parameters.AddWithValue("@started_at", startedAt.ToString("o"));
                await ftsCmd.ExecuteNonQueryAsync(ct);
            }
            catch (Exception ex)
            {
                AgentLogger.LogWarning("FTS indexing failed for message {Id}: {Error}", messageId, ex.Message);
            }
        }

        private async Task UpdateMessageFtsAsync(SqliteConnection conn, long messageId, string responseContent, string? toolName, CancellationToken ct)
        {
            try
            {
                // Get existing request content to combine
                await using var getCmd = conn.CreateCommand();
                getCmd.CommandText = "SELECT request_content FROM messages WHERE id = @id";
                getCmd.Parameters.AddWithValue("@id", messageId);
                var requestContent = await getCmd.ExecuteScalarAsync(ct) as string;

                // Delete old FTS entry
                await using var delCmd = conn.CreateCommand();
                delCmd.CommandText = "DELETE FROM messages_fts WHERE rowid = @id";
                delCmd.Parameters.AddWithValue("@id", messageId);
                await delCmd.ExecuteNonQueryAsync(ct);

                // Re-insert with combined content
                var combined = string.Join("\n", new[] { requestContent, responseContent }.Where(s => !string.IsNullOrWhiteSpace(s)));
                
                await using var getInfoCmd = conn.CreateCommand();
                getInfoCmd.CommandText = "SELECT session_id, message_type, started_at FROM messages WHERE id = @id";
                getInfoCmd.Parameters.AddWithValue("@id", messageId);
                await using var reader = await getInfoCmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    var sessionId = reader.GetString(0);
                    var messageType = reader.GetString(1);
                    var startedAt = DateTime.Parse(reader.GetString(2));
                    await IndexMessageInFtsAsync(conn, messageId, sessionId, messageType, combined, toolName, startedAt, ct);
                }
            }
            catch (Exception ex)
            {
                AgentLogger.LogWarning("FTS update failed for message {Id}: {Error}", messageId, ex.Message);
            }
        }

        /// <summary>
        /// Search past messages using full-text search. Returns matches across all sessions,
        /// prioritizing the current session. Searches both request and response content.
        /// </summary>
        public async Task<List<MemorySearchResult>> SearchMemoryAsync(
            string query, string? currentSessionId = null, int limit = 10, 
            bool includeCurrentContext = false, CancellationToken ct = default)
        {
            await using var conn = await GetConnectionAsync(ct);
            
            // Build FTS query — escape special characters for safety
            var ftsQuery = EscapeFtsQuery(query);
            if (string.IsNullOrWhiteSpace(ftsQuery))
                return new List<MemorySearchResult>();

            await using var cmd = conn.CreateCommand();
            
            // Search with ranking, prioritize current session via ORDER BY
            cmd.CommandText = @"
                SELECT 
                    f.rowid,
                    f.session_id,
                    f.message_type,
                    snippet(messages_fts, 2, '>>>', '<<<', '...', 48) as snippet,
                    f.tool_name,
                    f.started_at,
                    m.is_summarized,
                    m.response_content,
                    m.request_content,
                    s.title as session_title,
                    rank
                FROM messages_fts f
                JOIN messages m ON m.id = f.rowid
                LEFT JOIN sessions s ON s.session_id = f.session_id
                WHERE messages_fts MATCH @query
                ORDER BY 
                    CASE WHEN f.session_id = @current_session THEN 0 ELSE 1 END,
                    rank
                LIMIT @limit
            ";
            cmd.Parameters.AddWithValue("@query", ftsQuery);
            cmd.Parameters.AddWithValue("@current_session", (object?)currentSessionId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@limit", limit);

            var results = new List<MemorySearchResult>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var sessionId = reader.GetString(1);
                var isSummarized = !reader.IsDBNull(6) && reader.GetInt32(6) == 1;
                
                // Optionally skip messages that are in the current active context
                if (!includeCurrentContext && sessionId == currentSessionId && !isSummarized)
                    continue;

                results.Add(new MemorySearchResult
                {
                    MessageId = reader.GetInt64(0),
                    SessionId = sessionId,
                    MessageType = reader.GetString(2),
                    Snippet = reader.GetString(3),
                    ToolName = reader.IsDBNull(4) ? null : reader.GetString(4),
                    Timestamp = DateTime.Parse(reader.GetString(5)),
                    IsSummarized = isSummarized,
                    ResponseContent = reader.IsDBNull(7) ? null : reader.GetString(7),
                    RequestContent = reader.IsDBNull(8) ? null : reader.GetString(8),
                    SessionTitle = reader.IsDBNull(9) ? null : reader.GetString(9),
                    IsCurrentSession = sessionId == currentSessionId
                });
            }
            return results;
        }

        /// <summary>
        /// Rebuild the FTS index from existing messages. Call once after upgrade.
        /// </summary>
        public async Task RebuildFtsIndexAsync(CancellationToken ct = default)
        {
            await using var conn = await GetConnectionAsync(ct);
            
            // Clear existing FTS data
            await using var clearCmd = conn.CreateCommand();
            clearCmd.CommandText = "DELETE FROM messages_fts";
            await clearCmd.ExecuteNonQueryAsync(ct);
            
            // Re-index all messages that have content
            await using var selectCmd = conn.CreateCommand();
            selectCmd.CommandText = @"
                SELECT id, session_id, message_type, 
                       COALESCE(request_content, '') || CHAR(10) || COALESCE(response_content, '') as content,
                       tool_name, started_at
                FROM messages
                WHERE (request_content IS NOT NULL AND request_content != '')
                   OR (response_content IS NOT NULL AND response_content != '')
                ORDER BY started_at ASC
            ";
            
            var count = 0;
            await using var reader = await selectCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var content = reader.GetString(3).Trim();
                if (string.IsNullOrWhiteSpace(content)) continue;
                
                await IndexMessageInFtsAsync(conn, reader.GetInt64(0), reader.GetString(1),
                    reader.GetString(2), content, 
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    DateTime.Parse(reader.GetString(5)), ct);
                count++;
            }
            
            AgentLogger.LogInfo("FTS index rebuilt: {Count} messages indexed", count);
        }

        private static string EscapeFtsQuery(string query)
        {
            // Convert natural language query to FTS5 query
            // Split into words, wrap each in quotes for exact matching, join with OR for broad search
            var words = query.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return "";
            
            // If it looks like a phrase (3+ words), search as phrase first
            if (words.Length >= 3)
            {
                var escaped = query.Replace("\"", "\"\"");
                return $"\"{escaped}\"";
            }
            
            // For 1-2 words, use prefix matching with *
            return string.Join(" ", words.Select(w => 
            {
                var escaped = w.Replace("\"", "\"\"");
                return $"\"{escaped}\"*";
            }));
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
        public DateTime LastIndexedAt { get; set; } = DateTime.MinValue;
    }

    /// <summary>
    /// Persisted session data for storing agent sessions in SQLite.
    /// </summary>
    public class SessionData
    {
        public string SessionId { get; set; } = "";
        public string? AgentId { get; set; }
        public string? SystemPrompt { get; set; }
        public string? ModelId { get; set; }
        public string AgentRole { get; set; } = "main";
        public string? Title { get; set; }
        public string? WorkDirectory { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime LastActivityAt { get; set; } = DateTime.Now;
        public string? MetadataJson { get; set; }
        
        // Computed properties (not stored directly)
        public int MessageCount { get; set; }
    }

    /// <summary>
    /// Message record for tracking all LLM interactions including sub-agent hierarchy.
    /// </summary>
    public class MessageRecord
    {
        public long Id { get; set; }
        public string SessionId { get; set; } = "";
        public string? AgentId { get; set; }
        public long? ParentMessageId { get; set; }
        
        // Timing
        public DateTime StartedAt { get; set; } = DateTime.Now;
        public DateTime? CompletedAt { get; set; }
        public long? DurationMs { get; set; }
        
        // Agent Info
        public string? AgentRole { get; set; }
        public int AgentDepth { get; set; }
        public string? ModelId { get; set; }
        public string? SystemPromptId { get; set; }
        
        // Message type and direction
        public string MessageType { get; set; } = "user"; // 'user', 'assistant', 'tool_call', 'tool_result', 'delegation', 'summary', 'system'
        
        // Request
        public string? RequestContent { get; set; }
        public string? ContextMode { get; set; }
        public int? ContextTokenCount { get; set; }
        
        // Response
        public string? ResponseContent { get; set; }
        public string? ResponseSummary { get; set; }
        
        // Tool calls
        public string? ToolName { get; set; }
        public string? ToolArgsJson { get; set; }
        public string? ToolResultJson { get; set; }
        
        // Files tracking
        public string? FilesModifiedJson { get; set; }
        public string? FilesCreatedJson { get; set; }
        
        // Token metrics
        public int? PromptTokens { get; set; }
        public int? CompletionTokens { get; set; }
        public int? TotalTokens { get; set; }
        
        // Iteration tracking
        public int IterationNumber { get; set; }
        
        // Bailout tracking
        public int? MaxIterations { get; set; }
        public long? MaxDurationMs { get; set; }
        public string? BailoutReason { get; set; }
        
        // Status
        public string Status { get; set; } = "pending"; // 'pending', 'running', 'completed', 'failed', 'cancelled', 'timeout'
        public string? ErrorMessage { get; set; }
        
        // Summarization tracking
        public bool IsSummarized { get; set; } // True if this message was included in a summary
        public long? SummaryId { get; set; }   // Reference to the summary message that replaced this
        
        // Metadata
        public string? MetadataJson { get; set; }
    }

    /// <summary>
    /// Information for completing a message record.
    /// </summary>
    public class MessageCompleteInfo
    {
        public DateTime CompletedAt { get; set; } = DateTime.Now;
        public long DurationMs { get; set; }
        public string? ResponseContent { get; set; }
        public string? ResponseSummary { get; set; }
        public string? ToolName { get; set; }
        public string? ToolArgsJson { get; set; }
        public string? ToolResultJson { get; set; }
        public string? FilesModifiedJson { get; set; }
        public string? FilesCreatedJson { get; set; }
        public int? PromptTokens { get; set; }
        public int? CompletionTokens { get; set; }
        public int? TotalTokens { get; set; }
        public int IterationNumber { get; set; }
    }

    /// <summary>
    /// Token usage statistics for a session.
    /// </summary>
    public class SessionTokenStats
    {
        public int MessageCount { get; set; }
        public long TotalPromptTokens { get; set; }
        public long TotalCompletionTokens { get; set; }
        public long TotalTokens { get; set; }
        public long TotalDurationMs { get; set; }
        public int MaxDepth { get; set; }
    }

    public class MemorySearchResult
    {
        public long MessageId { get; set; }
        public string SessionId { get; set; } = "";
        public string MessageType { get; set; } = "";
        public string Snippet { get; set; } = "";
        public string? ToolName { get; set; }
        public DateTime Timestamp { get; set; }
        public bool IsSummarized { get; set; }
        public string? ResponseContent { get; set; }
        public string? RequestContent { get; set; }
        public string? SessionTitle { get; set; }
        public bool IsCurrentSession { get; set; }
    }

    #endregion
}
