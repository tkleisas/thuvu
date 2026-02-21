using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace thuvu.Models
{
    /// <summary>
    /// Manages agent jobs with SQLite persistence.
    /// Supports one active job per agent with journal updates.
    /// </summary>
    public sealed class AgentJobService
    {
        private static AgentJobService? _instance;
        private static readonly object _lock = new();

        public static AgentJobService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        _instance ??= new AgentJobService();
                    }
                }
                return _instance;
            }
        }

        private string? _connectionString;
        private bool _initialized;
        private AgentJob? _currentJob;
        private readonly object _jobLock = new();

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        /// <summary>
        /// Initialize the job service with SQLite database.
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

            // Create jobs table
            var schema = @"
                CREATE TABLE IF NOT EXISTS agent_jobs (
                    id TEXT PRIMARY KEY,
                    status TEXT NOT NULL,
                    prompt TEXT NOT NULL,
                    result TEXT,
                    error TEXT,
                    submitted_at TEXT NOT NULL,
                    started_at TEXT,
                    completed_at TEXT,
                    journal TEXT NOT NULL DEFAULT '[]'
                );

                CREATE INDEX IF NOT EXISTS idx_jobs_status ON agent_jobs(status);
                CREATE INDEX IF NOT EXISTS idx_jobs_submitted ON agent_jobs(submitted_at DESC);
            ";

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = schema;
            await cmd.ExecuteNonQueryAsync(ct);

            // Load current running job if any (for recovery after restart)
            await LoadCurrentJobAsync(ct);

            _initialized = true;
            AgentLogger.LogInfo("Agent job service initialized");
        }

        private async Task<SqliteConnection> GetConnectionAsync(CancellationToken ct = default)
        {
            if (!_initialized)
                await InitializeAsync(ct);

            var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            return conn;
        }

        private async Task LoadCurrentJobAsync(CancellationToken ct)
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();

            cmd.CommandText = @"
                SELECT id, status, prompt, result, error, submitted_at, started_at, completed_at, journal
                FROM agent_jobs
                WHERE status IN ('pending', 'running')
                ORDER BY submitted_at DESC
                LIMIT 1
            ";

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                _currentJob = ReadJob(reader);
                AgentLogger.LogInfo("Recovered job {JobId} with status {Status}", _currentJob.Id, _currentJob.Status);
            }
        }

        /// <summary>
        /// Check if the agent is currently busy with a job.
        /// </summary>
        public bool IsBusy => _currentJob != null && (_currentJob.Status == JobStatus.Pending || _currentJob.Status == JobStatus.Running);

        /// <summary>
        /// Get the current job (if any).
        /// </summary>
        public AgentJob? CurrentJob => _currentJob;

        // --- Streaming event support ---
        private Channel<AgentStreamEvent>? _eventChannel;
        private readonly object _channelLock = new();

        /// <summary>
        /// Create a new event channel for the current job. Previous channel is completed.
        /// </summary>
        public Channel<AgentStreamEvent> CreateEventChannel()
        {
            lock (_channelLock)
            {
                _eventChannel?.Writer.TryComplete();
                _eventChannel = Channel.CreateUnbounded<AgentStreamEvent>(new UnboundedChannelOptions
                {
                    SingleReader = false,
                    SingleWriter = true
                });
                return _eventChannel;
            }
        }

        /// <summary>
        /// Get a reader for the current event channel. Returns null if no active channel.
        /// </summary>
        public ChannelReader<AgentStreamEvent>? GetEventReader()
        {
            lock (_channelLock)
            {
                return _eventChannel?.Reader;
            }
        }

        /// <summary>
        /// Write an event to the current channel (fire and forget).
        /// </summary>
        public void EmitEvent(AgentStreamEvent evt)
        {
            lock (_channelLock)
            {
                _eventChannel?.Writer.TryWrite(evt);
            }
        }

        /// <summary>
        /// Complete the current event channel (signals end of stream).
        /// </summary>
        public void CompleteEventChannel()
        {
            lock (_channelLock)
            {
                _eventChannel?.Writer.TryComplete();
                _eventChannel = null;
            }
        }

        /// <summary>
        /// Submit a new job. Returns null if agent is busy.
        /// </summary>
        public async Task<AgentJob?> SubmitJobAsync(string prompt, CancellationToken ct = default,
            string? modelOverride = null, string? systemPromptOverride = null)
        {
            lock (_jobLock)
            {
                if (IsBusy)
                    return null;
            }

            var job = new AgentJob
            {
                Id = Guid.NewGuid().ToString("N")[..12],
                Status = JobStatus.Pending,
                Prompt = prompt,
                ModelOverride = modelOverride,
                SystemPromptOverride = systemPromptOverride,
                SubmittedAt = DateTime.UtcNow,
                Journal = new List<JournalEntry>()
            };

            await using var conn = await GetConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();

            cmd.CommandText = @"
                INSERT INTO agent_jobs (id, status, prompt, submitted_at, journal)
                VALUES (@id, @status, @prompt, @submitted_at, @journal)
            ";

            cmd.Parameters.AddWithValue("@id", job.Id);
            cmd.Parameters.AddWithValue("@status", job.Status.ToString().ToLower());
            cmd.Parameters.AddWithValue("@prompt", job.Prompt);
            cmd.Parameters.AddWithValue("@submitted_at", job.SubmittedAt.ToString("O"));
            cmd.Parameters.AddWithValue("@journal", JsonSerializer.Serialize(job.Journal, _jsonOptions));

            await cmd.ExecuteNonQueryAsync(ct);

            lock (_jobLock)
            {
                _currentJob = job;
            }

            AgentLogger.LogInfo("Job {JobId} submitted", job.Id);
            return job;
        }

        /// <summary>
        /// Start processing the current job.
        /// </summary>
        public async Task StartJobAsync(CancellationToken ct = default)
        {
            if (_currentJob == null || _currentJob.Status != JobStatus.Pending)
                return;

            _currentJob.Status = JobStatus.Running;
            _currentJob.StartedAt = DateTime.UtcNow;

            await UpdateJobInDbAsync(_currentJob, ct);
            AgentLogger.LogInfo("Job {JobId} started", _currentJob.Id);
        }

        /// <summary>
        /// Add a journal entry to the current job.
        /// </summary>
        public async Task AddJournalEntryAsync(string entry, CancellationToken ct = default)
        {
            if (_currentJob == null)
                return;

            var journalEntry = new JournalEntry
            {
                Timestamp = DateTime.UtcNow,
                Entry = entry
            };

            _currentJob.Journal.Add(journalEntry);
            await UpdateJobInDbAsync(_currentJob, ct);
        }

        /// <summary>
        /// Complete the current job with a result.
        /// </summary>
        public async Task CompleteJobAsync(string result, CancellationToken ct = default)
        {
            if (_currentJob == null)
                return;

            _currentJob.Status = JobStatus.Completed;
            _currentJob.CompletedAt = DateTime.UtcNow;
            _currentJob.Result = result;

            await UpdateJobInDbAsync(_currentJob, ct);
            await CleanupOldJobsAsync(ct);

            AgentLogger.LogInfo("Job {JobId} completed", _currentJob.Id);

            lock (_jobLock)
            {
                _currentJob = null;
            }
        }

        /// <summary>
        /// Fail the current job with an error.
        /// </summary>
        public async Task FailJobAsync(string error, CancellationToken ct = default)
        {
            if (_currentJob == null)
                return;

            _currentJob.Status = JobStatus.Failed;
            _currentJob.CompletedAt = DateTime.UtcNow;
            _currentJob.Error = error;

            await UpdateJobInDbAsync(_currentJob, ct);

            AgentLogger.LogError("Job {JobId} failed: {Error}", _currentJob.Id, error);

            lock (_jobLock)
            {
                _currentJob = null;
            }
        }

        /// <summary>
        /// Cancel the current job.
        /// </summary>
        public async Task CancelJobAsync(CancellationToken ct = default)
        {
            if (_currentJob == null)
                return;

            _currentJob.Status = JobStatus.Cancelled;
            _currentJob.CompletedAt = DateTime.UtcNow;

            await UpdateJobInDbAsync(_currentJob, ct);

            AgentLogger.LogInfo("Job {JobId} cancelled", _currentJob.Id);

            lock (_jobLock)
            {
                _currentJob = null;
            }
        }

        /// <summary>
        /// Cancel a specific job by ID.
        /// </summary>
        public async Task<bool> CancelJobByIdAsync(string jobId, CancellationToken ct = default)
        {
            if (_currentJob?.Id == jobId)
            {
                await CancelJobAsync(ct);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Get a job by ID.
        /// </summary>
        public async Task<AgentJob?> GetJobAsync(string jobId, CancellationToken ct = default)
        {
            // Check current job first
            if (_currentJob?.Id == jobId)
                return _currentJob;

            await using var conn = await GetConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();

            cmd.CommandText = @"
                SELECT id, status, prompt, result, error, submitted_at, started_at, completed_at, journal
                FROM agent_jobs
                WHERE id = @id
            ";
            cmd.Parameters.AddWithValue("@id", jobId);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                return ReadJob(reader);
            }
            return null;
        }

        /// <summary>
        /// Get recent job history.
        /// </summary>
        public async Task<List<AgentJob>> GetRecentJobsAsync(int limit = 50, CancellationToken ct = default)
        {
            await using var conn = await GetConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();

            cmd.CommandText = @"
                SELECT id, status, prompt, result, error, submitted_at, started_at, completed_at, journal
                FROM agent_jobs
                WHERE status IN ('completed', 'failed', 'cancelled')
                ORDER BY submitted_at DESC
                LIMIT @limit
            ";
            cmd.Parameters.AddWithValue("@limit", limit);

            var jobs = new List<AgentJob>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                jobs.Add(ReadJob(reader));
            }
            return jobs;
        }

        private async Task UpdateJobInDbAsync(AgentJob job, CancellationToken ct)
        {
            await using var conn = await GetConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();

            cmd.CommandText = @"
                UPDATE agent_jobs
                SET status = @status,
                    result = @result,
                    error = @error,
                    started_at = @started_at,
                    completed_at = @completed_at,
                    journal = @journal
                WHERE id = @id
            ";

            cmd.Parameters.AddWithValue("@id", job.Id);
            cmd.Parameters.AddWithValue("@status", job.Status.ToString().ToLower());
            cmd.Parameters.AddWithValue("@result", (object?)job.Result ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@error", (object?)job.Error ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@started_at", job.StartedAt?.ToString("O") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@completed_at", job.CompletedAt?.ToString("O") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@journal", JsonSerializer.Serialize(job.Journal, _jsonOptions));

            await cmd.ExecuteNonQueryAsync(ct);
        }

        private async Task CleanupOldJobsAsync(CancellationToken ct)
        {
            var maxHistory = AgentApiConfig.Instance.MaxJobHistory;
            if (maxHistory <= 0) return;

            await using var conn = await GetConnectionAsync(ct);
            await using var cmd = conn.CreateCommand();

            // Keep only the most recent N completed jobs
            cmd.CommandText = @"
                DELETE FROM agent_jobs
                WHERE id IN (
                    SELECT id FROM agent_jobs
                    WHERE status IN ('completed', 'failed', 'cancelled')
                    ORDER BY submitted_at DESC
                    LIMIT -1 OFFSET @offset
                )
            ";
            cmd.Parameters.AddWithValue("@offset", maxHistory);

            var deleted = await cmd.ExecuteNonQueryAsync(ct);
            if (deleted > 0)
            {
                AgentLogger.LogInfo("Cleaned up {Count} old jobs", deleted);
            }
        }

        private static AgentJob ReadJob(SqliteDataReader reader)
        {
            var job = new AgentJob
            {
                Id = reader.GetString(0),
                Status = Enum.Parse<JobStatus>(reader.GetString(1), ignoreCase: true),
                Prompt = reader.GetString(2),
                Result = reader.IsDBNull(3) ? null : reader.GetString(3),
                Error = reader.IsDBNull(4) ? null : reader.GetString(4),
                SubmittedAt = DateTime.Parse(reader.GetString(5)),
                StartedAt = reader.IsDBNull(6) ? null : DateTime.Parse(reader.GetString(6)),
                CompletedAt = reader.IsDBNull(7) ? null : DateTime.Parse(reader.GetString(7)),
                Journal = JsonSerializer.Deserialize<List<JournalEntry>>(reader.GetString(8), _jsonOptions) ?? new()
            };
            return job;
        }
    }

    /// <summary>
    /// Represents a job submitted to an agent.
    /// </summary>
    public class AgentJob
    {
        public string Id { get; set; } = "";
        public JobStatus Status { get; set; }
        public string Prompt { get; set; } = "";
        public string? ModelOverride { get; set; }
        public string? SystemPromptOverride { get; set; }
        public string? Result { get; set; }
        public string? Error { get; set; }
        public DateTime SubmittedAt { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public List<JournalEntry> Journal { get; set; } = new();

        /// <summary>
        /// Duration of the job (running or total).
        /// </summary>
        public TimeSpan? Duration
        {
            get
            {
                if (CompletedAt.HasValue && StartedAt.HasValue)
                    return CompletedAt.Value - StartedAt.Value;
                if (StartedAt.HasValue)
                    return DateTime.UtcNow - StartedAt.Value;
                return null;
            }
        }
    }

    /// <summary>
    /// A timestamped entry in the job journal.
    /// </summary>
    public class JournalEntry
    {
        public DateTime Timestamp { get; set; }
        public string Entry { get; set; } = "";
    }

    /// <summary>
    /// Status of an agent job.
    /// </summary>
    public enum JobStatus
    {
        Pending,
        Running,
        Completed,
        Failed,
        Cancelled
    }

    /// <summary>
    /// A real-time streaming event emitted during job processing.
    /// Used by the SSE endpoint to push updates to connected clients.
    /// </summary>
    public class AgentStreamEvent
    {
        public string Type { get; set; } = "";
        public string Data { get; set; } = "";

        public static AgentStreamEvent Token(string text) => new() { Type = "token", Data = JsonSerializer.Serialize(new { text }) };
        public static AgentStreamEvent Reasoning(string text) => new() { Type = "reasoning", Data = JsonSerializer.Serialize(new { text }) };
        public static AgentStreamEvent ToolCall(string name, string args) => new() { Type = "tool_call", Data = JsonSerializer.Serialize(new { name, args }) };
        public static AgentStreamEvent ToolComplete(string name, string args, string result, double elapsedSeconds) =>
            new() { Type = "tool_complete", Data = JsonSerializer.Serialize(new { name, args, result, elapsed = elapsedSeconds }) };
        public static AgentStreamEvent ContentReplace(string content) => new() { Type = "content_replace", Data = JsonSerializer.Serialize(new { content }) };
        public static AgentStreamEvent Complete(string response) => new() { Type = "complete", Data = JsonSerializer.Serialize(new { response }) };
        public static AgentStreamEvent Error(string message) => new() { Type = "error", Data = JsonSerializer.Serialize(new { message }) };
        public static AgentStreamEvent UsageInfo(object usage) => new() { Type = "usage", Data = JsonSerializer.Serialize(usage) };
    }
}
