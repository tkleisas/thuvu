using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace thuvu.Models
{
    /// <summary>
    /// Represents a subtask in a decomposed task plan
    /// </summary>
    public class SubTask
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";
        
        [JsonPropertyName("title")]
        public string Title { get; set; } = "";
        
        [JsonPropertyName("description")]
        public string Description { get; set; } = "";
        
        [JsonPropertyName("type")]
        public SubTaskType Type { get; set; } = SubTaskType.Implementation;
        
        [JsonPropertyName("estimatedMinutes")]
        public int EstimatedMinutes { get; set; } = 5;
        
        [JsonPropertyName("complexity")]
        public TaskComplexity Complexity { get; set; } = TaskComplexity.Medium;
        
        [JsonPropertyName("dependencies")]
        public List<string> Dependencies { get; set; } = new();
        
        [JsonPropertyName("requiredTools")]
        public List<string> RequiredTools { get; set; } = new();
        
        [JsonPropertyName("filesAffected")]
        public List<string> FilesAffected { get; set; } = new();
        
        [JsonPropertyName("canParallelize")]
        public bool CanParallelize { get; set; } = false;
        
        [JsonPropertyName("status")]
        public SubTaskStatus Status { get; set; } = SubTaskStatus.Pending;
        
        [JsonPropertyName("assignedAgentId")]
        public string? AssignedAgentId { get; set; }
        
        [JsonPropertyName("retryCount")]
        public int RetryCount { get; set; } = 0;
        
        [JsonPropertyName("lastError")]
        public string? LastError { get; set; }
        
        [JsonPropertyName("useThinkingModel")]
        public bool UseThinkingModel { get; set; } = false;
    }
    
    public enum SubTaskType
    {
        Analysis,       // Understanding code, reading files
        Planning,       // Designing solution
        Implementation, // Writing code
        Testing,        // Writing/running tests
        Review,         // Code review, validation
        Documentation,  // Writing docs
        Refactoring,    // Improving existing code
        Configuration   // Config changes, setup
    }
    
    public enum TaskComplexity
    {
        Trivial,    // < 2 min, single file change
        Simple,     // 2-5 min, few files
        Medium,     // 5-15 min, multiple files
        Complex,    // 15-30 min, architectural changes
        VeryComplex // 30+ min, major feature
    }
    
    public enum SubTaskStatus
    {
        Pending,
        InProgress,
        Completed,
        Failed,
        Blocked,
        Skipped
    }
    
    /// <summary>
    /// Represents a complete task decomposition plan
    /// </summary>
    public class TaskPlan
    {
        [JsonPropertyName("taskId")]
        public string TaskId { get; set; } = Guid.NewGuid().ToString("N")[..8];
        
        [JsonPropertyName("originalRequest")]
        public string OriginalRequest { get; set; } = "";
        
        [JsonPropertyName("summary")]
        public string Summary { get; set; } = "";
        
        [JsonPropertyName("subtasks")]
        public List<SubTask> SubTasks { get; set; } = new();
        
        [JsonPropertyName("totalEstimatedMinutes")]
        public int TotalEstimatedMinutes => SubTasks.Sum(t => t.EstimatedMinutes);
        
        [JsonPropertyName("recommendedAgentCount")]
        public int RecommendedAgentCount { get; set; } = 1;
        
        [JsonPropertyName("parallelizationStrategy")]
        public string ParallelizationStrategy { get; set; } = "";
        
        [JsonPropertyName("riskAssessment")]
        public string RiskAssessment { get; set; } = "";
        
        [JsonPropertyName("createdAt")]
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        
        /// <summary>
        /// Get subtasks that can be executed now (dependencies satisfied)
        /// </summary>
        public List<SubTask> GetReadySubTasks()
        {
            var completedIds = SubTasks
                .Where(t => t.Status == SubTaskStatus.Completed)
                .Select(t => t.Id)
                .ToHashSet();
                
            return SubTasks
                .Where(t => t.Status == SubTaskStatus.Pending)
                .Where(t => t.Dependencies.All(d => completedIds.Contains(d)))
                .ToList();
        }
        
        /// <summary>
        /// Get parallelizable groups of subtasks
        /// </summary>
        /// <summary>
        /// Get parallel execution groups for pending tasks
        /// </summary>
        /// <param name="includeFailedAsRetry">If true, include failed tasks for retry</param>
        /// <param name="treatFailedAsSatisfied">If true, treat failed dependencies as satisfied (allows downstream tasks to run)</param>
        public List<List<SubTask>> GetParallelGroups(bool includeFailedAsRetry = false, bool treatFailedAsSatisfied = false, bool resetInProgress = true)
        {
            var groups = new List<List<SubTask>>();
            
            // Reset InProgress tasks to Pending (they were interrupted)
            if (resetInProgress)
            {
                foreach (var task in SubTasks.Where(t => t.Status == SubTaskStatus.InProgress))
                {
                    SessionLogger.Instance.LogInfo($"Resetting interrupted task {task.Id} from InProgress to Pending");
                    task.Status = SubTaskStatus.Pending;
                    task.AssignedAgentId = null;
                }
            }
            
            // Determine which tasks to process
            var remaining = SubTasks
                .Where(t => t.Status == SubTaskStatus.Pending || 
                           (includeFailedAsRetry && t.Status == SubTaskStatus.Failed) ||
                           (includeFailedAsRetry && t.Status == SubTaskStatus.Blocked))
                .ToList();
            
            // Reset blocked tasks to pending if we're retrying
            if (includeFailedAsRetry)
            {
                foreach (var task in remaining.Where(t => t.Status == SubTaskStatus.Blocked))
                {
                    task.Status = SubTaskStatus.Pending;
                }
            }
            
            // Start with already completed tasks as satisfied dependencies
            var satisfied = SubTasks
                .Where(t => t.Status == SubTaskStatus.Completed)
                .Select(t => t.Id)
                .ToHashSet();
            
            // Optionally treat failed tasks as satisfied (allows downstream to proceed)
            if (treatFailedAsSatisfied)
            {
                foreach (var task in SubTasks.Where(t => t.Status == SubTaskStatus.Failed || t.Status == SubTaskStatus.Skipped))
                {
                    satisfied.Add(task.Id);
                }
            }
            
            while (remaining.Any())
            {
                var ready = remaining
                    .Where(t => t.Dependencies.All(d => satisfied.Contains(d)))
                    .ToList();
                    
                if (!ready.Any()) break; // Circular dependency or error
                
                groups.Add(ready);
                foreach (var task in ready)
                {
                    satisfied.Add(task.Id);
                    remaining.Remove(task);
                }
            }
            
            return groups;
        }
        
        /// <summary>
        /// Get count of tasks by status
        /// </summary>
        public (int pending, int completed, int failed, int blocked, int inProgress) GetStatusCounts()
        {
            return (
                SubTasks.Count(t => t.Status == SubTaskStatus.Pending),
                SubTasks.Count(t => t.Status == SubTaskStatus.Completed),
                SubTasks.Count(t => t.Status == SubTaskStatus.Failed),
                SubTasks.Count(t => t.Status == SubTaskStatus.Blocked),
                SubTasks.Count(t => t.Status == SubTaskStatus.InProgress)
            );
        }
        
        /// <summary>
        /// Reset failed and blocked tasks to pending for retry
        /// </summary>
        /// <returns>Number of tasks reset</returns>
        public int ResetFailedTasks()
        {
            int count = 0;
            foreach (var task in SubTasks)
            {
                // Reset Failed, Blocked, and InProgress (interrupted) tasks
                if (task.Status == SubTaskStatus.Failed || 
                    task.Status == SubTaskStatus.Blocked ||
                    task.Status == SubTaskStatus.InProgress)
                {
                    var wasInProgress = task.Status == SubTaskStatus.InProgress;
                    task.Status = SubTaskStatus.Pending;
                    task.AssignedAgentId = null;
                    
                    // Only increment retry count for actual failures, not interrupted tasks
                    if (!wasInProgress)
                    {
                        task.RetryCount++;
                        
                        // Escalate to thinking model after first failure for complex tasks
                        if (task.RetryCount >= 1 && 
                            (task.Complexity >= TaskComplexity.Complex || task.RetryCount >= 2))
                        {
                            task.UseThinkingModel = true;
                            SessionLogger.Instance.LogInfo($"Task {task.Id} will use thinking model on retry (attempt {task.RetryCount + 1})");
                        }
                    }
                    else
                    {
                        SessionLogger.Instance.LogInfo($"Task {task.Id} reset from InProgress to Pending (was interrupted)");
                    }
                    
                    count++;
                }
            }
            return count;
        }
        
        /// <summary>
        /// Check if orchestration can make progress (any tasks can run)
        /// </summary>
        public bool CanMakeProgress()
        {
            return GetReadySubTasks().Any();
        }
        
        // Lock file path for cross-process coordination
        private static string GetLockFilePath(string filePath) => filePath + ".lock";
        
        // In-process semaphore for thread coordination
        private static readonly SemaphoreSlim _inProcessLock = new SemaphoreSlim(1, 1);
        
        /// <summary>
        /// Acquire cross-process file lock with retry
        /// </summary>
        private static async Task<FileStream?> AcquireFileLockAsync(string filePath, int timeoutMs = 30000, CancellationToken ct = default)
        {
            var lockPath = GetLockFilePath(filePath);
            var dir = Path.GetDirectoryName(lockPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var retryDelay = 50;
            
            while (stopwatch.ElapsedMilliseconds < timeoutMs)
            {
                ct.ThrowIfCancellationRequested();
                
                try
                {
                    // Try to create/open lock file with exclusive access
                    var lockStream = new FileStream(
                        lockPath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        4096,
                        FileOptions.DeleteOnClose);
                    
                    // Write lock holder info for debugging
                    var lockInfo = $"{Environment.ProcessId}:{DateTime.UtcNow:O}";
                    var bytes = Encoding.UTF8.GetBytes(lockInfo);
                    await lockStream.WriteAsync(bytes, 0, bytes.Length, ct);
                    await lockStream.FlushAsync(ct);
                    
                    return lockStream;
                }
                catch (IOException)
                {
                    // File is locked by another process, wait and retry
                    await Task.Delay(retryDelay, ct);
                    retryDelay = Math.Min(retryDelay * 2, 500); // Exponential backoff up to 500ms
                }
            }
            
            SessionLogger.Instance.LogError($"Failed to acquire file lock for {filePath} after {timeoutMs}ms");
            return null;
        }
        
        /// <summary>
        /// Save plan to JSON file with cross-process file locking
        /// </summary>
        public void SaveToFile(string filePath)
        {
            SaveToFileAsync(filePath, CancellationToken.None).GetAwaiter().GetResult();
        }
        
        /// <summary>
        /// Save plan to JSON file asynchronously with cross-process file locking
        /// </summary>
        public async Task SaveToFileAsync(string filePath, CancellationToken ct = default)
        {
            // First acquire in-process lock
            await _inProcessLock.WaitAsync(ct);
            try
            {
                // Then acquire cross-process lock
                using var lockStream = await AcquireFileLockAsync(filePath, 30000, ct);
                if (lockStream == null)
                {
                    throw new IOException($"Could not acquire lock for {filePath}");
                }
                
                // Ensure directory exists
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions 
                { 
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
                
                // Write to temp file first, then rename (atomic operation)
                var tempPath = filePath + ".tmp." + Environment.ProcessId;
                await File.WriteAllTextAsync(tempPath, json, ct);
                
                // Use File.Move with overwrite for atomic replacement
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
                File.Move(tempPath, filePath);
                
                SessionLogger.Instance.LogInfo($"Plan saved to {filePath}");
            }
            finally
            {
                _inProcessLock.Release();
            }
        }
        
        /// <summary>
        /// Load plan from JSON file with cross-process file locking
        /// </summary>
        public static TaskPlan? LoadFromFile(string filePath)
        {
            return LoadFromFileAsync(filePath, CancellationToken.None).GetAwaiter().GetResult();
        }
        
        /// <summary>
        /// Load plan from JSON file asynchronously with cross-process file locking
        /// </summary>
        public static async Task<TaskPlan?> LoadFromFileAsync(string filePath, CancellationToken ct = default)
        {
            if (!File.Exists(filePath))
                return null;
            
            // First acquire in-process lock
            await _inProcessLock.WaitAsync(ct);
            try
            {
                // Then acquire cross-process lock
                using var lockStream = await AcquireFileLockAsync(filePath, 30000, ct);
                if (lockStream == null)
                {
                    SessionLogger.Instance.LogError($"Could not acquire lock to read {filePath}");
                    return null;
                }
                
                var json = await File.ReadAllTextAsync(filePath, ct);
                return JsonSerializer.Deserialize<TaskPlan>(json, new JsonSerializerOptions 
                { 
                    PropertyNameCaseInsensitive = true 
                });
            }
            finally
            {
                _inProcessLock.Release();
            }
        }
        
        /// <summary>
        /// Update a subtask status with cross-process file locking (atomic read-modify-write)
        /// </summary>
        public static async Task UpdateSubTaskStatusAsync(string filePath, string taskId, SubTaskStatus status, string? agentId = null, CancellationToken ct = default)
        {
            // First acquire in-process lock
            await _inProcessLock.WaitAsync(ct);
            try
            {
                // Then acquire cross-process lock
                using var lockStream = await AcquireFileLockAsync(filePath, 30000, ct);
                if (lockStream == null)
                {
                    SessionLogger.Instance.LogError($"Could not acquire lock to update {filePath}");
                    return;
                }
                
                // Read current plan
                if (!File.Exists(filePath))
                    return;
                    
                var json = await File.ReadAllTextAsync(filePath, ct);
                var plan = JsonSerializer.Deserialize<TaskPlan>(json, new JsonSerializerOptions 
                { 
                    PropertyNameCaseInsensitive = true 
                });
                
                if (plan == null) return;
                
                // Update the task
                var task = plan.SubTasks.FirstOrDefault(t => t.Id == taskId);
                if (task != null)
                {
                    task.Status = status;
                    if (agentId != null)
                        task.AssignedAgentId = agentId;
                    
                    // Save back
                    var updatedJson = JsonSerializer.Serialize(plan, new JsonSerializerOptions 
                    { 
                        WriteIndented = true,
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    });
                    
                    var tempPath = filePath + ".tmp." + Environment.ProcessId;
                    await File.WriteAllTextAsync(tempPath, updatedJson, ct);
                    
                    if (File.Exists(filePath))
                        File.Delete(filePath);
                    File.Move(tempPath, filePath);
                    
                    SessionLogger.Instance.LogInfo($"Task {taskId} status updated to {status}");
                }
            }
            finally
            {
                _inProcessLock.Release();
            }
        }
        
        /// <summary>
        /// Get the default plan file path in the current directory
        /// </summary>
        public static string GetDefaultPlanPath()
        {
            // Save plan in current directory (where user is running thuvu)
            // not in work subdirectory
            return Path.Combine(Directory.GetCurrentDirectory(), "current-plan.json");
        }
        
        /// <summary>
        /// Save plan as human-readable markdown
        /// </summary>
        public void SaveToMarkdown(string filePath)
        {
            // Ensure directory exists
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            
            var sb = new StringBuilder();
            sb.AppendLine($"# Task Plan: {TaskId}");
            sb.AppendLine();
            sb.AppendLine($"**Created:** {CreatedAt:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();
            sb.AppendLine("## Original Request");
            sb.AppendLine();
            sb.AppendLine(OriginalRequest);
            sb.AppendLine();
            sb.AppendLine("## Summary");
            sb.AppendLine();
            sb.AppendLine(Summary);
            sb.AppendLine();
            sb.AppendLine("## Metrics");
            sb.AppendLine();
            sb.AppendLine($"- **Recommended Agents:** {RecommendedAgentCount}");
            sb.AppendLine($"- **Estimated Time:** {TotalEstimatedMinutes} minutes");
            sb.AppendLine($"- **Total Subtasks:** {SubTasks.Count}");
            sb.AppendLine();
            sb.AppendLine("## Parallelization Strategy");
            sb.AppendLine();
            sb.AppendLine(ParallelizationStrategy);
            sb.AppendLine();
            sb.AppendLine("## Risk Assessment");
            sb.AppendLine();
            sb.AppendLine(RiskAssessment);
            sb.AppendLine();
            sb.AppendLine("## Subtasks");
            sb.AppendLine();
            
            var groups = GetParallelGroups();
            int phaseNum = 1;
            
            foreach (var group in groups)
            {
                var parallelNote = group.Count > 1 ? $" *(can run {group.Count} in parallel)*" : "";
                sb.AppendLine($"### Phase {phaseNum++}{parallelNote}");
                sb.AppendLine();
                
                foreach (var task in group)
                {
                    var statusIcon = task.Status switch
                    {
                        SubTaskStatus.Pending => "⬜",
                        SubTaskStatus.InProgress => "🔄",
                        SubTaskStatus.Completed => "✅",
                        SubTaskStatus.Failed => "❌",
                        SubTaskStatus.Blocked => "🚫",
                        SubTaskStatus.Skipped => "⏭️",
                        _ => "❓"
                    };
                    
                    sb.AppendLine($"#### {statusIcon} {task.Id}: {task.Title}");
                    sb.AppendLine();
                    sb.AppendLine($"- **Type:** {task.Type}");
                    sb.AppendLine($"- **Complexity:** {task.Complexity}");
                    sb.AppendLine($"- **Est. Time:** {task.EstimatedMinutes} min");
                    sb.AppendLine($"- **Status:** {task.Status}");
                    
                    if (task.Dependencies.Any())
                        sb.AppendLine($"- **Depends on:** {string.Join(", ", task.Dependencies)}");
                    
                    if (task.FilesAffected.Any())
                        sb.AppendLine($"- **Files:** {string.Join(", ", task.FilesAffected)}");
                    
                    if (!string.IsNullOrEmpty(task.AssignedAgentId))
                        sb.AppendLine($"- **Assigned to:** {task.AssignedAgentId}");
                    
                    sb.AppendLine();
                    sb.AppendLine($"**Description:** {task.Description}");
                    sb.AppendLine();
                }
            }
            
            File.WriteAllText(filePath, sb.ToString());
        }
    }
    
    /// <summary>
    /// Decomposes complex tasks into manageable subtasks using LLM
    /// </summary>
    public class TaskDecomposer
    {
        private readonly HttpClient _http;
        private readonly string _model;
        private readonly ModelEndpoint? _modelEndpoint;
        private readonly bool _useThinkingModel;
        
        private const string DecompositionPrompt = @"You are a task decomposition expert for software development projects.

Analyze the following task and break it down into subtasks. For each subtask, provide:
- A unique ID (e.g., ""t1"", ""t2"")
- A clear title
- A brief description
- The type of work (analysis, planning, implementation, testing, review, documentation, refactoring, configuration)
- Estimated time in minutes
- Complexity (trivial, simple, medium, complex, veryComplex)
- Dependencies (list of subtask IDs that must complete first)
- Required tools (from: search_files, read_file, write_file, apply_patch, run_process, dotnet_build, dotnet_test, git_status, git_diff)
- Files likely affected (patterns like ""src/*.cs"", ""tests/*.cs"")
- Whether it can run in parallel with other independent tasks

Also provide:
- A summary of the overall task
- Recommended number of agents (1-4) based on parallelization potential
- A parallelization strategy explanation
- Risk assessment (what could go wrong)

Respond ONLY with valid JSON matching this schema:
{
  ""summary"": ""string"",
  ""recommendedAgentCount"": number,
  ""parallelizationStrategy"": ""string"",
  ""riskAssessment"": ""string"",
  ""subtasks"": [
    {
      ""id"": ""string"",
      ""title"": ""string"",
      ""description"": ""string"",
      ""type"": ""analysis|planning|implementation|testing|review|documentation|refactoring|configuration"",
      ""estimatedMinutes"": number,
      ""complexity"": ""trivial|simple|medium|complex|veryComplex"",
      ""dependencies"": [""string""],
      ""requiredTools"": [""string""],
      ""filesAffected"": [""string""],
      ""canParallelize"": boolean
    }
  ]
}";

        public TaskDecomposer(HttpClient http, string? model = null, bool useThinkingModel = true)
        {
            _useThinkingModel = useThinkingModel;
            
            // Try to use thinking model for better planning
            if (useThinkingModel)
            {
                _modelEndpoint = ModelRegistry.Instance.GetThinkingModel();
                if (_modelEndpoint != null)
                {
                    _http = _modelEndpoint.CreateHttpClient();
                    _model = _modelEndpoint.ModelId;
                    SessionLogger.Instance.LogInfo($"TaskDecomposer using thinking model: {_modelEndpoint.DisplayName}");
                    return;
                }
            }
            
            // Fallback to provided http client and model
            _http = http;
            _model = model ?? AgentConfig.Config.Model;
            SessionLogger.Instance.LogInfo($"TaskDecomposer using default model: {_model}");
        }
        
        /// <summary>
        /// Decompose a task into subtasks
        /// </summary>
        public async Task<TaskPlan> DecomposeAsync(string taskDescription, string? codebaseContext = null, CancellationToken ct = default)
        {
            var userPrompt = new StringBuilder();
            userPrompt.AppendLine("## Task to decompose:");
            userPrompt.AppendLine(taskDescription);
            
            if (!string.IsNullOrEmpty(codebaseContext))
            {
                userPrompt.AppendLine();
                userPrompt.AppendLine("## Codebase context:");
                userPrompt.AppendLine(codebaseContext);
            }
            
            var messages = new List<ChatMessage>
            {
                new("system", DecompositionPrompt),
                new("user", userPrompt.ToString())
            };
            
            var request = new ChatRequest
            {
                Model = _model,
                Messages = messages,
                Temperature = 0.3f // Lower temperature for more consistent structured output
            };
            
            // Set max_tokens for thinking model if configured
            if (_modelEndpoint?.MaxOutputTokens > 0)
            {
                request.Max_Tokens = _modelEndpoint.MaxOutputTokens;
            }
            
            var json = JsonSerializer.Serialize(request, new JsonSerializerOptions 
            { 
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
            
            SessionLogger.Instance.LogInfo($"Sending decomposition request to {_model}");
            
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync("/v1/chat/completions", content, ct);
            response.EnsureSuccessStatusCode();
            
            var responseJson = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(responseJson);
            
            // Handle thinking model response which may have reasoning_content
            var messageElement = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message");
            
            var assistantContent = "";
            
            // Check for reasoning_content (thinking model)
            if (messageElement.TryGetProperty("reasoning_content", out var reasoningEl))
            {
                var reasoning = reasoningEl.GetString() ?? "";
                SessionLogger.Instance.LogInfo($"Thinking model reasoning: {reasoning.Length} characters");
            }
            
            // Get the actual content
            if (messageElement.TryGetProperty("content", out var contentEl))
            {
                assistantContent = contentEl.GetString() ?? "";
            }
            
            if (string.IsNullOrEmpty(assistantContent))
            {
                SessionLogger.Instance.LogError("Empty response from decomposition model");
                throw new InvalidOperationException("Empty response from decomposition model");
            }
            
            // Extract JSON from response (handle markdown code blocks)
            var planJson = ExtractJson(assistantContent);
            
            var plan = ParseTaskPlan(planJson, taskDescription);
            return plan;
        }
        
        /// <summary>
        /// Quick estimate without full decomposition
        /// </summary>
        public async Task<(int agentCount, string rationale)> EstimateAgentCountAsync(string taskDescription, CancellationToken ct = default)
        {
            var plan = await DecomposeAsync(taskDescription, null, ct);
            return (plan.RecommendedAgentCount, plan.ParallelizationStrategy);
        }
        
        private string ExtractJson(string content)
        {
            // Try to extract JSON from markdown code blocks
            var jsonStart = content.IndexOf("```json");
            if (jsonStart >= 0)
            {
                jsonStart = content.IndexOf('\n', jsonStart) + 1;
                var jsonEnd = content.IndexOf("```", jsonStart);
                if (jsonEnd > jsonStart)
                {
                    return content[jsonStart..jsonEnd].Trim();
                }
            }
            
            // Try plain code block
            jsonStart = content.IndexOf("```");
            if (jsonStart >= 0)
            {
                jsonStart = content.IndexOf('\n', jsonStart) + 1;
                var jsonEnd = content.IndexOf("```", jsonStart);
                if (jsonEnd > jsonStart)
                {
                    return content[jsonStart..jsonEnd].Trim();
                }
            }
            
            // Try to find raw JSON
            jsonStart = content.IndexOf('{');
            if (jsonStart >= 0)
            {
                var jsonEnd = content.LastIndexOf('}');
                if (jsonEnd > jsonStart)
                {
                    return content[jsonStart..(jsonEnd + 1)];
                }
            }
            
            return content;
        }
        
        private TaskPlan ParseTaskPlan(string json, string originalRequest)
        {
            var plan = new TaskPlan
            {
                OriginalRequest = originalRequest
            };
            
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                
                plan.Summary = root.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "";
                plan.RecommendedAgentCount = root.TryGetProperty("recommendedAgentCount", out var r) ? r.GetInt32() : 1;
                plan.ParallelizationStrategy = root.TryGetProperty("parallelizationStrategy", out var p) ? p.GetString() ?? "" : "";
                plan.RiskAssessment = root.TryGetProperty("riskAssessment", out var ra) ? ra.GetString() ?? "" : "";
                
                if (root.TryGetProperty("subtasks", out var subtasks))
                {
                    foreach (var st in subtasks.EnumerateArray())
                    {
                        var subtask = new SubTask
                        {
                            Id = st.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                            Title = st.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                            Description = st.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                            EstimatedMinutes = st.TryGetProperty("estimatedMinutes", out var em) ? em.GetInt32() : 5,
                            CanParallelize = st.TryGetProperty("canParallelize", out var cp) && cp.GetBoolean()
                        };
                        
                        // Parse enums
                        if (st.TryGetProperty("type", out var typeEl))
                        {
                            var typeStr = typeEl.GetString() ?? "implementation";
                            subtask.Type = Enum.TryParse<SubTaskType>(typeStr, true, out var type) ? type : SubTaskType.Implementation;
                        }
                        
                        if (st.TryGetProperty("complexity", out var compEl))
                        {
                            var compStr = compEl.GetString() ?? "medium";
                            subtask.Complexity = Enum.TryParse<TaskComplexity>(compStr, true, out var comp) ? comp : TaskComplexity.Medium;
                        }
                        
                        // Parse arrays
                        if (st.TryGetProperty("dependencies", out var deps))
                        {
                            subtask.Dependencies = deps.EnumerateArray().Select(e => e.GetString() ?? "").ToList();
                        }
                        
                        if (st.TryGetProperty("requiredTools", out var tools))
                        {
                            subtask.RequiredTools = tools.EnumerateArray().Select(e => e.GetString() ?? "").ToList();
                        }
                        
                        if (st.TryGetProperty("filesAffected", out var files))
                        {
                            subtask.FilesAffected = files.EnumerateArray().Select(e => e.GetString() ?? "").ToList();
                        }
                        
                        plan.SubTasks.Add(subtask);
                    }
                }
            }
            catch (JsonException ex)
            {
                // If parsing fails, create a single task
                plan.Summary = "Failed to parse decomposition, treating as single task";
                plan.SubTasks.Add(new SubTask
                {
                    Id = "t1",
                    Title = "Execute task",
                    Description = originalRequest,
                    Type = SubTaskType.Implementation,
                    Complexity = TaskComplexity.Medium,
                    EstimatedMinutes = 15
                });
                SessionLogger.Instance.LogError($"Failed to parse task plan: {ex.Message}");
            }
            
            return plan;
        }
    }
    
    /// <summary>
    /// Extension methods for printing task plans
    /// </summary>
    public static class TaskPlanPrinter
    {
        public static void PrintPlan(TaskPlan plan)
        {
            const int boxWidth = 80;
            var line = new string('═', boxWidth - 2);
            
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"╔{line}╗");
            PrintBoxLine("Task Decomposition Plan", boxWidth);
            Console.WriteLine($"╠{line}╣");
            Console.ResetColor();
            
            // Summary
            PrintBoxLine($"Task: {Truncate(plan.OriginalRequest, 70)}", boxWidth);
            PrintBoxLine($"Summary: {Truncate(plan.Summary, 68)}", boxWidth);
            PrintBoxLine("", boxWidth);
            
            // Metrics
            Console.ForegroundColor = ConsoleColor.Yellow;
            PrintBoxLine($"Recommended Agents: {plan.RecommendedAgentCount}  |  Est. Time: {plan.TotalEstimatedMinutes} min  |  Subtasks: {plan.SubTasks.Count}", boxWidth);
            Console.ResetColor();
            
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"╠{line}╣");
            Console.ResetColor();
            
            // Subtasks
            var groups = plan.GetParallelGroups();
            int groupNum = 1;
            
            foreach (var group in groups)
            {
                var parallelLabel = group.Count > 1 ? $" (can run {group.Count} in parallel)" : "";
                Console.ForegroundColor = ConsoleColor.DarkGray;
                PrintBoxLine($"── Phase {groupNum++}{parallelLabel} ──", boxWidth);
                Console.ResetColor();
                
                foreach (var task in group)
                {
                    var icon = task.Type switch
                    {
                        SubTaskType.Analysis => "[A]",
                        SubTaskType.Planning => "[P]",
                        SubTaskType.Implementation => "[I]",
                        SubTaskType.Testing => "[T]",
                        SubTaskType.Review => "[R]",
                        SubTaskType.Documentation => "[D]",
                        SubTaskType.Refactoring => "[F]",
                        SubTaskType.Configuration => "[C]",
                        _ => "[?]"
                    };
                    
                    var complexityColor = task.Complexity switch
                    {
                        TaskComplexity.Trivial => ConsoleColor.Green,
                        TaskComplexity.Simple => ConsoleColor.Green,
                        TaskComplexity.Medium => ConsoleColor.Yellow,
                        TaskComplexity.Complex => ConsoleColor.Red,
                        TaskComplexity.VeryComplex => ConsoleColor.Magenta,
                        _ => ConsoleColor.White
                    };
                    
                    Console.Write("║  ");
                    Console.ForegroundColor = ConsoleColor.White;
                    Console.Write($"{icon} {task.Id}: ");
                    Console.ForegroundColor = complexityColor;
                    Console.Write(Truncate(task.Title, 50).PadRight(50));
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write($" ~{task.EstimatedMinutes}min");
                    Console.ResetColor();
                    var padding = boxWidth - 2 - 3 - 4 - task.Id.Length - 2 - 50 - 6 - task.EstimatedMinutes.ToString().Length;
                    Console.WriteLine(new string(' ', Math.Max(0, padding)) + "║");
                    
                    if (task.Dependencies.Any())
                    {
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        PrintBoxLine($"     └─ depends on: {string.Join(", ", task.Dependencies)}", boxWidth);
                        Console.ResetColor();
                    }
                }
            }
            
            // Risk assessment
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"╠{line}╣");
            Console.ResetColor();
            
            Console.ForegroundColor = ConsoleColor.Yellow;
            PrintBoxLine("Risk Assessment:", boxWidth);
            Console.ResetColor();
            
            var riskLines = WordWrap(plan.RiskAssessment, boxWidth - 6);
            foreach (var riskLine in riskLines)
            {
                PrintBoxLine($"  {riskLine}", boxWidth);
            }
            
            // Strategy
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"╠{line}╣");
            Console.ResetColor();
            
            Console.ForegroundColor = ConsoleColor.Green;
            PrintBoxLine("Parallelization Strategy:", boxWidth);
            Console.ResetColor();
            
            var stratLines = WordWrap(plan.ParallelizationStrategy, boxWidth - 6);
            foreach (var stratLine in stratLines)
            {
                PrintBoxLine($"  {stratLine}", boxWidth);
            }
            
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"╚{line}╝");
            Console.ResetColor();
            Console.WriteLine();
        }
        
        private static string Truncate(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (text.Length <= maxLength) return text;
            return text[..(maxLength - 3)] + "...";
        }
        
        private static void PrintBoxLine(string text, int boxWidth)
        {
            var innerWidth = boxWidth - 2;
            var textArea = innerWidth - 2;
            var truncated = Truncate(text, textArea);
            var padding = textArea - truncated.Length;
            
            Console.Write("║ ");
            Console.Write(truncated);
            Console.Write(new string(' ', padding));
            Console.WriteLine("║");
        }
        
        private static List<string> WordWrap(string text, int maxWidth)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(text)) return lines;
            
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var currentLine = new StringBuilder();
            
            foreach (var word in words)
            {
                if (currentLine.Length + word.Length + 1 > maxWidth)
                {
                    if (currentLine.Length > 0)
                    {
                        lines.Add(currentLine.ToString());
                        currentLine.Clear();
                    }
                }
                
                if (currentLine.Length > 0)
                    currentLine.Append(' ');
                currentLine.Append(word);
            }
            
            if (currentLine.Length > 0)
                lines.Add(currentLine.ToString());
                
            return lines;
        }
    }
}
