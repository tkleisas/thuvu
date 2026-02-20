using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace thuvu.Models
{
    /// <summary>
    /// Represents the state of an agent in the pool
    /// </summary>
    public class AgentInstance
    {
        public string AgentId { get; set; } = "";
        public AgentState State { get; set; } = AgentState.Idle;
        public string? CurrentTaskId { get; set; }
        public Process? Process { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? LastActivityAt { get; set; }
        public string WorkDirectory { get; set; } = "";
        public string? AssignedBranch { get; set; }
        public int CompletedTasks { get; set; }
        public int FailedTasks { get; set; }
    }
    
    public enum AgentState
    {
        Idle,
        Starting,
        Running,
        Completed,
        Failed,
        Stopping,
        Stopped
    }
    
    /// <summary>
    /// Result from an agent executing a subtask
    /// </summary>
    public class AgentTaskResult
    {
        public string TaskId { get; set; } = "";
        public string AgentId { get; set; } = "";
        public bool Success { get; set; }
        public string? Result { get; set; }
        public string? Error { get; set; }
        public TimeSpan Duration { get; set; }
        public List<string> FilesModified { get; set; } = new();
        public List<string> ToolsUsed { get; set; } = new();
    }
    
    /// <summary>
    /// Configuration for the orchestrator
    /// </summary>
    public class OrchestratorConfig
    {
        public int MaxAgents { get; set; } = 4;
        public int AgentTimeoutMinutes { get; set; } = 30;
        public bool UseProcessIsolation { get; set; } = true;
        public bool AutoMergeResults { get; set; } = true;
        public string BaseBranch { get; set; } = "main";
        public bool RequireTestsPass { get; set; } = true;
    }
    
    /// <summary>
    /// Manages a pool of agent instances
    /// </summary>
    public class AgentPool : IDisposable
    {
        private readonly ConcurrentDictionary<string, AgentInstance> _agents = new();
        private readonly OrchestratorConfig _config;
        private readonly string _baseWorkDir;
        private int _agentCounter = 0;
        private bool _disposed;
        
        public AgentPool(OrchestratorConfig config, string? workDirectory = null)
        {
            _config = config;
            _baseWorkDir = workDirectory ?? AgentConfig.GetWorkDirectory();
        }
        
        public int ActiveCount => _agents.Values.Count(a => a.State == AgentState.Running);
        public int AvailableSlots => _config.MaxAgents - ActiveCount;
        public IEnumerable<AgentInstance> Agents => _agents.Values;
        
        /// <summary>
        /// Acquire an agent from the pool or create a new one
        /// </summary>
        public async Task<AgentInstance?> AcquireAgentAsync(string taskId, CancellationToken ct)
        {
            // Try to find an idle agent
            var idleAgent = _agents.Values.FirstOrDefault(a => a.State == AgentState.Idle);
            if (idleAgent != null)
            {
                idleAgent.State = AgentState.Running;
                idleAgent.CurrentTaskId = taskId;
                idleAgent.LastActivityAt = DateTime.Now;
                return idleAgent;
            }
            
            // Check if we can create a new agent
            if (ActiveCount >= _config.MaxAgents)
            {
                return null; // Pool is full
            }
            
            // Create new agent - use "Agent-N" format to match TUI tabs
            var agentId = $"Agent-{Interlocked.Increment(ref _agentCounter)}";
            var agent = new AgentInstance
            {
                AgentId = agentId,
                State = AgentState.Starting,
                CurrentTaskId = taskId,
                StartedAt = DateTime.Now,
                LastActivityAt = DateTime.Now,
                WorkDirectory = Path.Combine(_baseWorkDir, "agents", agentId)
            };
            
            // Create agent work directory
            Directory.CreateDirectory(agent.WorkDirectory);
            
            if (_config.UseProcessIsolation)
            {
                // Start agent as separate process
                try
                {
                    agent.Process = await StartAgentProcessAsync(agent, ct);
                    agent.State = AgentState.Running;
                }
                catch (Exception ex)
                {
                    agent.State = AgentState.Failed;
                    SessionLogger.Instance.LogError($"Failed to start agent {agentId}: {ex.Message}");
                    return null;
                }
            }
            else
            {
                agent.State = AgentState.Running;
            }
            
            _agents[agentId] = agent;
            SessionLogger.Instance.LogInfo($"Agent {agentId} started for task {taskId}");
            
            return agent;
        }
        
        /// <summary>
        /// Release an agent back to the pool
        /// </summary>
        public void ReleaseAgent(string agentId, bool success)
        {
            if (_agents.TryGetValue(agentId, out var agent))
            {
                if (success)
                    agent.CompletedTasks++;
                else
                    agent.FailedTasks++;
                    
                agent.State = AgentState.Idle;
                agent.CurrentTaskId = null;
                agent.LastActivityAt = DateTime.Now;
                
                SessionLogger.Instance.LogInfo($"Agent {agentId} released (success={success})");
            }
        }
        
        /// <summary>
        /// Stop a specific agent
        /// </summary>
        public async Task StopAgentAsync(string agentId)
        {
            if (_agents.TryRemove(agentId, out var agent))
            {
                agent.State = AgentState.Stopping;
                
                if (agent.Process != null && !agent.Process.HasExited)
                {
                    try
                    {
                        agent.Process.Kill(entireProcessTree: true);
                        await agent.Process.WaitForExitAsync();
                    }
                    catch { }
                    finally
                    {
                        agent.Process.Dispose();
                    }
                }
                
                agent.State = AgentState.Stopped;
                SessionLogger.Instance.LogInfo($"Agent {agentId} stopped");
            }
        }
        
        /// <summary>
        /// Stop all agents
        /// </summary>
        public async Task StopAllAsync()
        {
            var tasks = _agents.Keys.Select(id => StopAgentAsync(id));
            await Task.WhenAll(tasks);
        }
        
        private async Task<Process> StartAgentProcessAsync(AgentInstance agent, CancellationToken ct)
        {
            // Get the path to the current executable
            var exePath = Process.GetCurrentProcess().MainModule?.FileName 
                ?? throw new InvalidOperationException("Cannot determine executable path");
            
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"--agent-mode --agent-id {agent.AgentId} --work-dir \"{agent.WorkDirectory}\"",
                WorkingDirectory = agent.WorkDirectory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            
            // Copy environment
            psi.Environment["THUVU_AGENT_ID"] = agent.AgentId;
            psi.Environment["THUVU_ORCHESTRATED"] = "true";
            
            var process = new Process { StartInfo = psi };
            process.Start();
            
            // Wait for agent to signal ready
            var readyLine = await process.StandardOutput.ReadLineAsync(ct);
            if (readyLine != "AGENT_READY")
            {
                process.Kill();
                throw new InvalidOperationException($"Agent failed to start: {readyLine}");
            }
            
            return process;
        }
        
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            
            // Fire-and-forget stop to avoid synchronous deadlock risk
            _ = Task.Run(async () =>
            {
                try { await StopAllAsync(); }
                catch { }
            });
        }
    }
    
    /// <summary>
    /// Orchestrates multiple agents to execute a task plan
    /// </summary>
    public class TaskOrchestrator : IDisposable
    {
        private readonly AgentPool _pool;
        private readonly OrchestratorConfig _config;
        private readonly System.Net.Http.HttpClient _http;
        private readonly string _workDirectory;
        private readonly ConcurrentDictionary<string, AgentTaskResult> _results = new();
        private string? _planPath;
        private bool _disposed;
        
        public event Action<string, string>? OnAgentStarted;      // agentId, taskId
        public event Action<string, AgentTaskResult>? OnTaskCompleted; // agentId, result
        public event Action<string>? OnPhaseCompleted;            // phase description
        public event Action<TaskPlan, bool>? OnPlanCompleted;     // plan, success
        public event Action<string, string>? OnAgentOutput;       // agentId, text (streaming output)
        public event Action<string, string, string>? OnAgentToolCall; // agentId, toolName, status
        public event Action<string, ToolProgress>? OnAgentToolProgress; // agentId, progress
        
        public TaskOrchestrator(System.Net.Http.HttpClient http, OrchestratorConfig? config = null, string? workDirectory = null)
        {
            _http = http;
            _config = config ?? new OrchestratorConfig();
            _workDirectory = workDirectory ?? AgentConfig.GetWorkDirectory();
            _pool = new AgentPool(_config, _workDirectory);
        }
        
        /// <summary>
        /// Execute a complete task plan
        /// </summary>
        /// <param name="plan">The task plan to execute</param>
        /// <param name="ct">Cancellation token</param>
        /// <param name="planPath">Path to save plan progress</param>
        /// <param name="retryFailed">If true, retry failed tasks</param>
        /// <param name="skipFailed">If true, treat failed dependencies as satisfied and continue</param>
        public async Task<OrchestratorResult> ExecutePlanAsync(TaskPlan plan, CancellationToken ct, string? planPath = null, bool retryFailed = false, bool skipFailed = false)
        {
            _planPath = planPath ?? TaskPlan.GetDefaultPlanPath();
            
            var result = new OrchestratorResult
            {
                PlanId = plan.TaskId,
                StartedAt = DateTime.Now
            };
            
            SessionLogger.Instance.LogInfo($"Starting orchestration for plan {plan.TaskId} with {plan.SubTasks.Count} subtasks (retryFailed={retryFailed}, skipFailed={skipFailed})");
            
            try
            {
                // Create orchestration branch
                var orchBranch = $"orchestration/{plan.TaskId}";
                await CreateOrchestrationBranchAsync(orchBranch, ct).ConfigureAwait(false);
                
                int phaseNum = 1;
                
                // Dynamic phase execution - recalculate runnable tasks after each phase
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    
                    // Recalculate which tasks can run now (dependencies satisfied)
                    var groups = plan.GetParallelGroups(includeFailedAsRetry: retryFailed, treatFailedAsSatisfied: skipFailed);
                    
                    if (!groups.Any())
                    {
                        // No more runnable tasks
                        var (pending, completed, failed, blocked, inProgress) = plan.GetStatusCounts();
                        SessionLogger.Instance.LogInfo($"No more runnable tasks. Status: {pending} pending, {inProgress} in-progress, {completed} completed, {failed} failed, {blocked} blocked");
                        
                        if (pending == 0 && blocked == 0 && inProgress == 0)
                        {
                            // All tasks processed
                            break;
                        }
                        
                        if (failed > 0 && !retryFailed && blocked > 0)
                        {
                            result.Error = $"Cannot proceed. {failed} task(s) failed, {blocked} task(s) blocked. Use --retry to retry failed tasks, or --skip to proceed with downstream tasks.";
                        }
                        else if (blocked > 0 && !skipFailed)
                        {
                            result.Error = $"Cannot proceed. {blocked} task(s) are blocked by failed dependencies. Use --skip to proceed anyway.";
                        }
                        else if (inProgress > 0)
                        {
                            result.Error = $"Cannot proceed. {inProgress} task(s) are still marked as in-progress (interrupted). They will be reset on next run.";
                        }
                        break;
                    }
                    
                    // Get the first group of runnable tasks (all have satisfied dependencies)
                    var group = groups[0];
                    var totalGroups = groups.Count;
                    
                    var phaseDesc = $"Phase {phaseNum} ({totalGroups} group(s) remaining): {group.Count} task(s)";
                    SessionLogger.Instance.LogInfo($"Starting {phaseDesc}");
                    
                    // Execute tasks in this phase (potentially in parallel)
                    var phaseTasks = group.Select(subtask => ExecuteSubTaskAsync(subtask, plan, ct));
                    SessionLogger.Instance.LogInfo($"Waiting for {group.Count} task(s) in phase {phaseNum}...");
                    var phaseResults = await Task.WhenAll(phaseTasks).ConfigureAwait(false);
                    SessionLogger.Instance.LogInfo($"Phase {phaseNum} WhenAll completed with {phaseResults.Length} results");
                    
                    // Check for failures
                    var failures = phaseResults.Where(r => !r.Success).ToList();
                    if (failures.Any())
                    {
                        SessionLogger.Instance.LogError($"Phase {phaseNum} had {failures.Count} failure(s)");
                        
                        // Update subtask status
                        foreach (var failure in failures)
                        {
                            var subtask = plan.SubTasks.FirstOrDefault(t => t.Id == failure.TaskId);
                            if (subtask != null)
                                subtask.Status = SubTaskStatus.Failed;
                        }
                        
                        // Mark directly dependent tasks as blocked (only tasks that depend on failed ones)
                        var failedIds = failures.Select(f => f.TaskId).ToHashSet();
                        foreach (var subtask in plan.SubTasks.Where(t => t.Status == SubTaskStatus.Pending))
                        {
                            if (subtask.Dependencies.Any(d => failedIds.Contains(d)))
                            {
                                subtask.Status = SubTaskStatus.Blocked;
                                SessionLogger.Instance.LogInfo($"Task {subtask.Id} blocked due to failed dependency");
                            }
                        }
                    }
                    
                    // Collect results
                    foreach (var taskResult in phaseResults)
                    {
                        result.TaskResults.Add(taskResult);
                        _results[taskResult.TaskId] = taskResult;
                    }
                    
                    // Persist plan state after each phase
                    SessionLogger.Instance.LogInfo($"Saving plan progress after phase {phaseNum}...");
                    await SavePlanProgressAsync(plan, ct).ConfigureAwait(false);
                    SessionLogger.Instance.LogInfo($"Plan progress saved after phase {phaseNum}");
                    
                    OnPhaseCompleted?.Invoke(phaseDesc);
                    SessionLogger.Instance.LogInfo($"Phase {phaseNum} completed, moving to next phase");
                    phaseNum++;
                }
                
                SessionLogger.Instance.LogInfo($"All phases completed. Total results: {result.TaskResults.Count}");
                
                // Merge all agent branches if successful
                if (_config.AutoMergeResults && result.TaskResults.All(r => r.Success))
                {
                    await MergeAgentBranchesAsync(plan, orchBranch, ct).ConfigureAwait(false);
                    result.MergedSuccessfully = true;
                }
                
                result.Success = result.TaskResults.All(r => r.Success);
                result.CompletedAt = DateTime.Now;
                
                OnPlanCompleted?.Invoke(plan, result.Success);
            }
            catch (OperationCanceledException)
            {
                result.Success = false;
                result.Error = "Orchestration cancelled";
                SessionLogger.Instance.LogInfo("Orchestration cancelled by user");
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
                SessionLogger.Instance.LogError($"Orchestration failed: {ex.Message}");
            }
            finally
            {
                result.CompletedAt = DateTime.Now;
                // Final save of plan state
                await SavePlanProgressAsync(plan, CancellationToken.None).ConfigureAwait(false);
            }
            
            return result;
        }
        
        /// <summary>
        /// Save plan progress to file with locking
        /// </summary>
        private async Task SavePlanProgressAsync(TaskPlan plan, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(_planPath)) return;
            
            try
            {
                await plan.SaveToFileAsync(_planPath, ct);
                SessionLogger.Instance.LogInfo($"Plan progress saved: {plan.SubTasks.Count(t => t.Status == SubTaskStatus.Completed)} completed, {plan.SubTasks.Count(t => t.Status == SubTaskStatus.Failed)} failed");
            }
            catch (Exception ex)
            {
                SessionLogger.Instance.LogError($"Failed to save plan progress: {ex.Message}");
            }
        }
        
        private async Task<AgentTaskResult> ExecuteSubTaskAsync(SubTask subtask, TaskPlan plan, CancellationToken ct)
        {
            var result = new AgentTaskResult
            {
                TaskId = subtask.Id
            };
            
            var sw = Stopwatch.StartNew();
            AgentInstance? agent = null;
            
            try
            {
                // Wait for an available agent
                while ((agent = await _pool.AcquireAgentAsync(subtask.Id, ct).ConfigureAwait(false)) == null)
                {
                    await Task.Delay(500, ct).ConfigureAwait(false);
                }
                
                result.AgentId = agent.AgentId;
                subtask.AssignedAgentId = agent.AgentId;
                subtask.Status = SubTaskStatus.InProgress;
                
                // Save in-progress status
                if (!string.IsNullOrEmpty(_planPath))
                {
                    await TaskPlan.UpdateSubTaskStatusAsync(_planPath, subtask.Id, SubTaskStatus.InProgress, agent.AgentId, ct).ConfigureAwait(false);
                }
                
                OnAgentStarted?.Invoke(agent.AgentId, subtask.Id);
                SessionLogger.Instance.LogInfo($"Agent {agent.AgentId} executing subtask {subtask.Id}: {subtask.Title}");
                
                // Create agent branch
                var agentBranch = $"agent/{plan.TaskId}/{agent.AgentId}/{subtask.Id}";
                agent.AssignedBranch = agentBranch;
                
                if (_config.UseProcessIsolation && agent.Process != null)
                {
                    // Send task to agent process via stdin
                    result = await ExecuteViaProcessAsync(agent, subtask, plan, ct).ConfigureAwait(false);
                }
                else
                {
                    // Execute in-process
                    result = await ExecuteInProcessAsync(agent, subtask, plan, ct).ConfigureAwait(false);
                }
                
                subtask.Status = result.Success ? SubTaskStatus.Completed : SubTaskStatus.Failed;
            }
            catch (OperationCanceledException)
            {
                result.Success = false;
                result.Error = "Cancelled";
                subtask.Status = SubTaskStatus.Failed;
                subtask.LastError = "Cancelled";
                SessionLogger.Instance.LogInfo($"Task {subtask.Id} cancelled");
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
                subtask.Status = SubTaskStatus.Failed;
                subtask.LastError = ex.Message;
                SessionLogger.Instance.LogError($"Subtask {subtask.Id} failed with exception: {ex.Message}");
            }
            finally
            {
                sw.Stop();
                result.Duration = sw.Elapsed;
                
                SessionLogger.Instance.LogInfo($"Task {subtask.Id} status updated to {subtask.Status}");
                
                if (agent != null)
                {
                    _pool.ReleaseAgent(agent.AgentId, result.Success);
                }
                
                // Store error in subtask for retry context
                if (!result.Success && !string.IsNullOrEmpty(result.Error))
                {
                    subtask.LastError = result.Error;
                }
                
                // Save final status for this task with retry
                if (!string.IsNullOrEmpty(_planPath))
                {
                    for (int retry = 0; retry < 5; retry++)
                    {
                        try
                        {
                            await TaskPlan.UpdateSubTaskStatusAsync(_planPath, subtask.Id, subtask.Status, subtask.AssignedAgentId, CancellationToken.None);
                            SessionLogger.Instance.LogInfo($"Task {subtask.Id} status saved to plan file");
                            break;
                        }
                        catch (Exception ex)
                        {
                            SessionLogger.Instance.LogError($"Failed to save task {subtask.Id} status (attempt {retry + 1}/5): {ex.Message}");
                            if (retry < 4)
                                await Task.Delay(500 * (retry + 1)); // Exponential backoff
                        }
                    }
                }
                
                OnTaskCompleted?.Invoke(result.AgentId, result);
            }
            
            return result;
        }
        
        private async Task<AgentTaskResult> ExecuteViaProcessAsync(
            AgentInstance agent, SubTask subtask, TaskPlan plan, CancellationToken ct)
        {
            var command = new AgentCommand
            {
                Type = "execute_subtask",
                SubTask = subtask,
                PlanContext = new PlanContext
                {
                    PlanId = plan.TaskId,
                    OriginalRequest = plan.OriginalRequest,
                    PreviousResults = _results.Values.ToList()
                }
            };
            
            var json = JsonSerializer.Serialize(command, new JsonSerializerOptions 
            { 
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase 
            });
            
            // Send command to agent
            await agent.Process!.StandardInput.WriteLineAsync(json);
            await agent.Process.StandardInput.FlushAsync();
            
            // Read response with timeout
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(_config.AgentTimeoutMinutes));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            
            var responseLine = await agent.Process.StandardOutput.ReadLineAsync(linkedCts.Token);
            
            if (string.IsNullOrEmpty(responseLine))
            {
                return new AgentTaskResult
                {
                    TaskId = subtask.Id,
                    AgentId = agent.AgentId,
                    Success = false,
                    Error = "No response from agent"
                };
            }
            
            try
            {
                var response = JsonSerializer.Deserialize<AgentTaskResult>(responseLine, 
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return response ?? new AgentTaskResult
                {
                    TaskId = subtask.Id,
                    AgentId = agent.AgentId,
                    Success = false,
                    Error = "Invalid response from agent"
                };
            }
            catch (JsonException ex)
            {
                return new AgentTaskResult
                {
                    TaskId = subtask.Id,
                    AgentId = agent.AgentId,
                    Success = false,
                    Error = $"Failed to parse agent response: {ex.Message}"
                };
            }
        }
        
        private async Task<AgentTaskResult> ExecuteInProcessAsync(
            AgentInstance agent, SubTask subtask, TaskPlan plan, CancellationToken ct)
        {
            // Create agent-specific context for work directory isolation
            var maxContextLength = AgentConfig.Config.MaxContextLength > 0 
                ? AgentConfig.Config.MaxContextLength 
                : TokenTracker.Instance.MaxContextLength;
            var agentContext = AgentContext.CreateContext(agent.AgentId, _workDirectory, maxContextLength);
            agentContext.CurrentTaskId = subtask.Id;
            
            // Execute within agent context so tools use correct work directory
            return await AgentContext.RunInContextAsync(agentContext, async () =>
            {
                // Build prompt for the subtask
                var prompt = BuildSubTaskPrompt(subtask, plan);
                
                // Create isolated message history
                var messages = new List<ChatMessage>
                {
                    new("system", GetAgentSystemPrompt(agent, subtask)),
                    new("user", prompt)
                };
                
                // Get tool definitions
                var tools = Tools.BuildTools.GetToolsForSession();
                
                // Calculate iteration limit based on task complexity
                // Higher limits since real tasks often need many tool calls
                int maxIterations = subtask.Complexity switch
                {
                    TaskComplexity.Trivial => 20,
                    TaskComplexity.Simple => 35,
                    TaskComplexity.Medium => 50,
                    TaskComplexity.Complex => 75,
                    TaskComplexity.VeryComplex => 100,
                    _ => 50
                };
                
                SessionLogger.Instance.LogInfo($"[{agent.AgentId}] Starting task {subtask.Id} with {maxIterations} max iterations, context: {maxContextLength} tokens");
                
                // Capture agentId for callbacks
                var agentId = agent.AgentId;
                
                // Execute via agent loop with per-task iteration limit and streaming callbacks
                var response = await AgentLoop.CompleteWithToolsStreamingAsync(
                    _http, 
                    AgentConfig.Config.Model, 
                    messages, 
                    tools, 
                    ct,
                    onToken: token => OnAgentOutput?.Invoke(agentId, token),
                    onToolResult: (toolName, result) => OnAgentToolCall?.Invoke(agentId, toolName, result.Length > 100 ? result.Substring(0, 100) + "..." : result),
                    onUsage: null,
                    onToolComplete: (toolName, args, result, elapsed) => OnAgentToolCall?.Invoke(agentId, toolName, $"[{elapsed.TotalSeconds:F1}s] Done"),
                    onToolProgress: progress => OnAgentToolProgress?.Invoke(agentId, progress),
                    maxIterations: maxIterations).ConfigureAwait(false);
                
                // Log token usage from this agent's context
                var tokenTracker = agentContext.TokenTracker;
                SessionLogger.Instance.LogInfo($"[{agent.AgentId}] Task {subtask.Id} completed. Token usage: {tokenTracker.TotalTokens}/{tokenTracker.MaxContextLength} ({tokenTracker.UsagePercent:P0})");
                
                // Check if agent hit iteration limit or context limit
                bool hitIterLimit = response?.Contains("Maximum iteration limit") == true;
                bool hitContextLimit = tokenTracker.IsCritical;
                bool hitToolLoop = response?.Contains("stuck in tool call loop") == true;
                bool hitToolFailure = response?.Contains("failed") == true && response?.Contains("consecutively") == true;
                
                // Log detailed success/failure determination
                SessionLogger.Instance.LogInfo($"[{agent.AgentId}] Task {subtask.Id} status check: " +
                    $"hitIterLimit={hitIterLimit}, hitContextLimit={hitContextLimit}, " +
                    $"hitToolLoop={hitToolLoop}, hitToolFailure={hitToolFailure}, " +
                    $"responseNull={response == null}, responseLen={response?.Length ?? 0}");
                
                string? error = null;
                bool success = true;
                
                // Iteration limit is a soft warning - task may still be complete
                // Only hard failures are tool loops and consecutive tool failures
                if (hitIterLimit)
                {
                    // Log as warning but check context for actual completion indicators
                    SessionLogger.Instance.LogInfo($"[{agent.AgentId}] Task {subtask.Id} hit iteration limit - checking if work was completed");
                    
                    // If context usage is reasonable and no other failures, consider it a success with warning
                    if (!hitContextLimit && !hitToolLoop && !hitToolFailure && tokenTracker.UsagePercent < 0.90)
                    {
                        error = "Task hit iteration limit but appears to have completed work";
                        success = true; // Optimistic - the work was likely done
                        SessionLogger.Instance.LogInfo($"[{agent.AgentId}] Task {subtask.Id} marked as success despite iteration limit (context: {tokenTracker.UsagePercent:P0})");
                    }
                    else
                    {
                        error = "Task exceeded iteration limit and may be incomplete";
                        success = false;
                    }
                }
                else if (hitContextLimit)
                {
                    error = $"Task ran low on context tokens ({tokenTracker.RemainingTokens} remaining)";
                    success = false;
                }
                else if (hitToolLoop)
                {
                    error = "Agent stuck in tool call loop";
                    success = false;
                }
                else if (hitToolFailure)
                {
                    error = "Tool failed multiple times consecutively";
                    success = false;
                }
                else if (string.IsNullOrEmpty(response))
                {
                    // If response is null/empty but no error flags, assume success (task completed silently)
                    SessionLogger.Instance.LogInfo($"[{agent.AgentId}] Task {subtask.Id} completed with empty response, assuming success");
                }
                
                return new AgentTaskResult
                {
                    TaskId = subtask.Id,
                    AgentId = agent.AgentId,
                    Success = success,
                    Result = response,
                    Error = error
                };
            });
        }
        
        private string BuildSubTaskPrompt(SubTask subtask, TaskPlan plan)
        {
            var sb = new StringBuilder();
            
            // Task header
            sb.AppendLine($"# Execute Subtask: {subtask.Title}");
            sb.AppendLine();
            
            // Retry context if this is a retry attempt
            if (subtask.RetryCount > 0)
            {
                sb.AppendLine("## ⚠️ RETRY ATTEMPT");
                sb.AppendLine($"This is attempt #{subtask.RetryCount + 1} for this task.");
                if (!string.IsNullOrEmpty(subtask.LastError))
                {
                    sb.AppendLine($"**Previous error:** {subtask.LastError}");
                }
                sb.AppendLine();
                sb.AppendLine("**Important:** Analyze what went wrong in the previous attempt and try a different approach.");
                if (subtask.UseThinkingModel)
                {
                    sb.AppendLine("**Note:** This task has been escalated for deeper analysis. Take extra care to understand the problem before implementing.");
                }
                sb.AppendLine();
            }
            
            // Description
            sb.AppendLine("## Description");
            sb.AppendLine(subtask.Description);
            sb.AppendLine();
            
            // Task metadata
            sb.AppendLine("## Task Details");
            sb.AppendLine($"- **Task ID:** {subtask.Id}");
            sb.AppendLine($"- **Type:** {subtask.Type}");
            sb.AppendLine($"- **Complexity:** {subtask.Complexity}");
            sb.AppendLine($"- **Estimated Time:** {subtask.EstimatedMinutes} minutes");
            sb.AppendLine();
            
            // Files to work with
            if (subtask.FilesAffected.Any())
            {
                sb.AppendLine("## Target Files");
                sb.AppendLine("You should focus on these files/patterns:");
                foreach (var file in subtask.FilesAffected)
                {
                    sb.AppendLine($"- `{file}`");
                }
                sb.AppendLine();
            }
            
            // Context from completed dependencies
            var completedDeps = subtask.Dependencies
                .Where(d => _results.ContainsKey(d) && _results[d].Success)
                .Select(d => _results[d])
                .ToList();
                
            if (completedDeps.Any())
            {
                sb.AppendLine("## Context from Completed Dependencies");
                sb.AppendLine("These tasks have been completed. Use their output as context:");
                sb.AppendLine();
                foreach (var dep in completedDeps)
                {
                    sb.AppendLine($"### Task {dep.TaskId} (completed by {dep.AgentId})");
                    if (!string.IsNullOrEmpty(dep.Result))
                    {
                        // Include more context, truncate only if very long
                        var resultPreview = dep.Result.Length > 500 
                            ? dep.Result.Substring(0, 500) + "..." 
                            : dep.Result;
                        sb.AppendLine(resultPreview);
                    }
                    if (dep.FilesModified.Any())
                    {
                        sb.AppendLine($"Files modified: {string.Join(", ", dep.FilesModified)}");
                    }
                    sb.AppendLine();
                }
            }
            
            // Original request for full context
            sb.AppendLine("## Original Project Request");
            sb.AppendLine("This subtask is part of a larger project:");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine(plan.OriginalRequest);
            sb.AppendLine("```");
            sb.AppendLine();
            
            // Project summary
            if (!string.IsNullOrEmpty(plan.Summary))
            {
                sb.AppendLine("## Project Summary");
                sb.AppendLine(plan.Summary);
                sb.AppendLine();
            }
            
            // Instructions
            sb.AppendLine("## Instructions");
            sb.AppendLine("1. Use the tools available to complete this subtask");
            sb.AppendLine("2. Start by exploring the current state (search_files, read_file)");
            sb.AppendLine("3. Implement the required changes");
            sb.AppendLine("4. Verify your work compiles (dotnet_build if applicable)");
            sb.AppendLine("5. Provide a summary of what you accomplished");
            sb.AppendLine();
            sb.AppendLine("Begin working on this task now.");
            
            return sb.ToString();
        }
        
        private string GetAgentSystemPrompt(AgentInstance agent, SubTask subtask)
        {
            return $@"You are Agent {agent.AgentId}, an autonomous coding assistant that is part of a multi-agent orchestration system.

## CRITICAL: Work Directory
**Your work directory is: {_workDirectory}**

All file operations (search_files, read_file, write_file) operate relative to this directory.
You MUST work within this directory. Start by reading existing files to understand the codebase.

## Your Current Assignment
**Task ID:** {subtask.Id}
**Task:** {subtask.Title}
**Type:** {subtask.Type}
**Complexity:** {subtask.Complexity}

## Role & Responsibilities
You are responsible for completing your assigned subtask as part of a larger project. Other agents may be working on related tasks simultaneously. Your work will be merged with theirs upon completion.

## IMPORTANT: Implementation Rules
- You MUST write actual code, not plans or documentation about code
- Do NOT create planning documents, roadmaps, or design docs unless the task explicitly asks for documentation
- Actually implement the feature/fix by creating or modifying source code files
- Use tools to read existing code first, then make changes

## Guidelines

### 1. Stay Focused
- Complete ONLY your assigned subtask
- Do not attempt to do work assigned to other agents
- If you discover additional work needed, note it in your response but don't implement it
- Work ONLY in the work directory specified above

### 2. Use Tools Effectively
You have access to these tools: {string.Join(", ", subtask.RequiredTools)}

- **search_files**: Find files by glob pattern in work directory
- **read_file**: Read file contents (use relative paths from work directory)
- **write_file**: Create or overwrite files (use relative paths)
- **apply_patch**: Apply unified diff patches for surgical edits
- **run_process**: Execute commands (dotnet, git, etc.) - runs in work directory
- **dotnet_build**: Build .NET projects
- **dotnet_test**: Run tests
- **dotnet_new**: Create new .NET projects

### 3. Working on Existing Code
1. First use `search_files` to find relevant files
2. Use `read_file` to understand existing code structure
3. Use `apply_patch` for small edits to existing files
4. Use `write_file` for new files or complete rewrites
5. Run `dotnet_build` to verify your changes compile

### 4. Starting a New Project
If the directory is empty and the task requires creating a project:
1. Use `dotnet_new` or `run_process` to create the initial project structure
2. Create files relative to the work directory
3. Follow standard project layout conventions

### 5. Code Quality
- Write clean, well-structured code
- Follow existing project conventions if modifying existing code
- Add appropriate error handling
- Use meaningful names for variables, methods, and classes

### 6. File Operations
- Use RELATIVE paths from the work directory
- Always read a file before modifying it
- Use apply_patch for small changes to existing files
- Use write_file for new files or complete rewrites

### 7. Testing
- If your task involves creating code, consider if tests are needed
- Run dotnet_build to verify code compiles
- Run dotnet_test if tests exist and are relevant

### 8. Error Handling
- If a tool fails, analyze the error and try to fix it
- If you cannot complete the task, explain why clearly
- Don't give up on first failure - try alternative approaches

### 9. Task Completion Signal
**IMPORTANT:** When you have finished all work for your assigned task:
- Stop making tool calls
- Provide your summary
- Include the exact phrase ""thuvu Finished"" at the end of your response

## Output Format
After completing your task, provide a brief summary:
1. What was accomplished (files created/modified with relative paths)
2. Any issues encountered
3. Suggestions for dependent tasks (if any)

End with: ""thuvu Finished""

Remember: You are one agent in a team. Do your part well, and the orchestrator will handle coordination.";
        }
        
        private async Task CreateOrchestrationBranchAsync(string branchName, CancellationToken ct)
        {
            try
            {
                // First check if git is initialized in the work directory
                var gitDir = Path.Combine(_workDirectory, ".git");
                if (!Directory.Exists(gitDir))
                {
                    SessionLogger.Instance.LogInfo($"Initializing git repository in {_workDirectory}");
                    
                    // Initialize git repo
                    var initArgs = JsonSerializer.Serialize(new
                    {
                        cmd = "git",
                        args = new[] { "init" },
                        cwd = _workDirectory
                    });
                    await Tools.RunProcessToolImpl.RunProcessToolAsync(initArgs, ct);
                    
                    // Configure user for this repo
                    var configNameArgs = JsonSerializer.Serialize(new
                    {
                        cmd = "git",
                        args = new[] { "config", "user.name", "THUVU Agent" },
                        cwd = _workDirectory
                    });
                    await Tools.RunProcessToolImpl.RunProcessToolAsync(configNameArgs, ct);
                    
                    var configEmailArgs = JsonSerializer.Serialize(new
                    {
                        cmd = "git",
                        args = new[] { "config", "user.email", "agent@thuvu.local" },
                        cwd = _workDirectory
                    });
                    await Tools.RunProcessToolImpl.RunProcessToolAsync(configEmailArgs, ct);
                    
                    // Create initial commit with empty .gitkeep
                    var gitkeepPath = Path.Combine(_workDirectory, ".gitkeep");
                    if (!File.Exists(gitkeepPath))
                    {
                        await File.WriteAllTextAsync(gitkeepPath, "# THUVU generated project\n", ct);
                    }
                    
                    var addArgs = JsonSerializer.Serialize(new
                    {
                        cmd = "git",
                        args = new[] { "add", "." },
                        cwd = _workDirectory
                    });
                    await Tools.RunProcessToolImpl.RunProcessToolAsync(addArgs, ct);
                    
                    var commitArgs = JsonSerializer.Serialize(new
                    {
                        cmd = "git",
                        args = new[] { "commit", "-m", "Initial commit by THUVU orchestrator" },
                        cwd = _workDirectory
                    });
                    await Tools.RunProcessToolImpl.RunProcessToolAsync(commitArgs, ct);
                    
                    SessionLogger.Instance.LogInfo("Git repository initialized with initial commit");
                }
                
                // Now create the orchestration branch
                var args = JsonSerializer.Serialize(new
                {
                    cmd = "git",
                    args = new[] { "checkout", "-b", branchName },
                    cwd = _workDirectory
                });
                await Tools.RunProcessToolImpl.RunProcessToolAsync(args, ct);
                SessionLogger.Instance.LogInfo($"Created orchestration branch: {branchName}");
            }
            catch (Exception ex)
            {
                SessionLogger.Instance.LogError($"Failed to create orchestration branch: {ex.Message}");
            }
        }
        
        private async Task MergeAgentBranchesAsync(TaskPlan plan, string targetBranch, CancellationToken ct)
        {
            // Collect all agent branches
            var agentBranches = _pool.Agents
                .Where(a => !string.IsNullOrEmpty(a.AssignedBranch))
                .Select(a => a.AssignedBranch!)
                .ToList();
            
            // First checkout the target branch
            try
            {
                var checkoutArgs = JsonSerializer.Serialize(new
                {
                    cmd = "git",
                    args = new[] { "checkout", targetBranch },
                    cwd = _workDirectory
                });
                await Tools.RunProcessToolImpl.RunProcessToolAsync(checkoutArgs, ct);
            }
            catch (Exception ex)
            {
                SessionLogger.Instance.LogError($"Failed to checkout {targetBranch}: {ex.Message}");
                return;
            }
            
            foreach (var branch in agentBranches)
            {
                try
                {
                    var args = JsonSerializer.Serialize(new
                    {
                        cmd = "git",
                        args = new[] { "merge", "--no-ff", branch, "-m", $"Merge {branch}" },
                        cwd = _workDirectory
                    });
                    await Tools.RunProcessToolImpl.RunProcessToolAsync(args, ct);
                    SessionLogger.Instance.LogInfo($"Merged branch {branch}");
                }
                catch (Exception ex)
                {
                    SessionLogger.Instance.LogError($"Failed to merge {branch}: {ex.Message}");
                }
            }
        }
        
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _pool.Dispose();
        }
    }
    
    /// <summary>
    /// Command sent to agent process
    /// </summary>
    public class AgentCommand
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "";
        
        [JsonPropertyName("subtask")]
        public SubTask? SubTask { get; set; }
        
        [JsonPropertyName("planContext")]
        public PlanContext? PlanContext { get; set; }
    }
    
    /// <summary>
    /// Context passed to agents about the overall plan
    /// </summary>
    public class PlanContext
    {
        [JsonPropertyName("planId")]
        public string PlanId { get; set; } = "";
        
        [JsonPropertyName("originalRequest")]
        public string OriginalRequest { get; set; } = "";
        
        [JsonPropertyName("previousResults")]
        public List<AgentTaskResult> PreviousResults { get; set; } = new();
    }
    
    /// <summary>
    /// Overall result from orchestrating a plan
    /// </summary>
    public class OrchestratorResult
    {
        public string PlanId { get; set; } = "";
        public bool Success { get; set; }
        public string? Error { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime CompletedAt { get; set; }
        public TimeSpan Duration => CompletedAt - StartedAt;
        public List<AgentTaskResult> TaskResults { get; set; } = new();
        public bool MergedSuccessfully { get; set; }
        
        public int CompletedCount => TaskResults.Count(r => r.Success);
        public int FailedCount => TaskResults.Count(r => !r.Success);
    }
    
    /// <summary>
    /// Prints orchestration results - supports both console and callback output
    /// </summary>
    public static class OrchestratorPrinter
    {
        /// <summary>
        /// Print result to console (CLI mode)
        /// </summary>
        public static void PrintResult(OrchestratorResult result)
        {
            PrintResult(result, null);
        }
        
        /// <summary>
        /// Print result via callback (TUI mode) or console if callback is null
        /// </summary>
        public static void PrintResult(OrchestratorResult result, Action<string>? output)
        {
            var sb = new System.Text.StringBuilder();
            const int boxWidth = 60;
            var line = new string('═', boxWidth - 2);
            
            sb.AppendLine();
            sb.AppendLine($"╔{line}╗");
            AppendBoxLine(sb, result.Success ? "Orchestration Completed" : "Orchestration Failed", boxWidth);
            sb.AppendLine($"╠{line}╣");
            
            // Summary
            AppendBoxLine(sb, $"Plan ID: {result.PlanId}", boxWidth);
            AppendBoxLine(sb, $"Duration: {result.Duration.TotalMinutes:F1} minutes", boxWidth);
            AppendBoxLine(sb, $"Tasks: {result.CompletedCount} completed, {result.FailedCount} failed", boxWidth);
            
            if (result.MergedSuccessfully)
            {
                AppendBoxLine(sb, "All changes merged successfully", boxWidth);
            }
            
            sb.AppendLine($"╠{line}╣");
            
            // Task details
            AppendBoxLine(sb, "Task Results:", boxWidth);
            
            foreach (var taskResult in result.TaskResults)
            {
                var icon = taskResult.Success ? "[OK]" : "[XX]";
                var taskLine = $"  {icon} {taskResult.TaskId} ({taskResult.AgentId}) - {taskResult.Duration.TotalSeconds:F1}s";
                AppendBoxLine(sb, taskLine, boxWidth);
                
                if (!taskResult.Success && !string.IsNullOrEmpty(taskResult.Error))
                {
                    AppendBoxLine(sb, $"     Error: {Truncate(taskResult.Error, 45)}", boxWidth);
                }
            }
            
            sb.AppendLine($"╚{line}╝");
            sb.AppendLine();
            
            var text = sb.ToString();
            
            if (output != null)
            {
                output(text);
            }
            else
            {
                // Console output with colors
                Console.ForegroundColor = result.Success ? ConsoleColor.Green : ConsoleColor.Red;
                Console.Write(text);
                Console.ResetColor();
            }
        }
        
        /// <summary>
        /// Get result as plain text (for TUI)
        /// </summary>
        public static string GetResultText(OrchestratorResult result)
        {
            var sb = new System.Text.StringBuilder();
            PrintResult(result, s => sb.Append(s));
            return sb.ToString();
        }
        
        private static string Truncate(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (text.Length <= maxLength) return text;
            return text[..(maxLength - 3)] + "...";
        }
        
        private static void AppendBoxLine(System.Text.StringBuilder sb, string text, int boxWidth)
        {
            var innerWidth = boxWidth - 2;
            var textArea = innerWidth - 2;
            var truncated = Truncate(text, textArea);
            var padding = Math.Max(0, textArea - truncated.Length);
            
            sb.Append("║ ");
            sb.Append(truncated);
            sb.Append(new string(' ', padding));
            sb.AppendLine(" ║");
        }
    }
}
