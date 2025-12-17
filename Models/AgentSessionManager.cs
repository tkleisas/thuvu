using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace thuvu.Models
{
    /// <summary>
    /// Represents a checkpoint in the agent's work
    /// </summary>
    public class AgentCheckpoint
    {
        public string Tag { get; set; } = "";
        public string CommitSha { get; set; } = "";
        public DateTime Timestamp { get; set; }
        public string Message { get; set; } = "";
        public bool TestsPassed { get; set; }
    }

    /// <summary>
    /// Agent session state for git isolation
    /// </summary>
    public class AgentSession
    {
        public string AgentId { get; set; } = "";
        public string TaskDescription { get; set; } = "";
        public string BranchName { get; set; } = "";
        public string BaseBranch { get; set; } = "main";
        public List<AgentCheckpoint> Checkpoints { get; set; } = new();
        public string Status { get; set; } = "initialized";
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public int CommitCount { get; set; }
        
        [JsonIgnore]
        public string WorkDirectory { get; set; } = "";
    }

    /// <summary>
    /// Manages agent sessions with git branch isolation
    /// </summary>
    public static class AgentSessionManager
    {
        private static AgentSession? _currentSession;
        private static readonly object _lock = new();
        private static int _sessionCounter = 0;

        /// <summary>
        /// Current active session
        /// </summary>
        public static AgentSession? CurrentSession
        {
            get { lock (_lock) return _currentSession; }
        }

        /// <summary>
        /// Generate a unique agent ID
        /// </summary>
        public static string GenerateAgentId()
        {
            _sessionCounter++;
            return $"thuvu-{_sessionCounter:D3}";
        }

        /// <summary>
        /// Start a new agent session with git branch isolation
        /// </summary>
        public static async Task<AgentSession> StartSessionAsync(
            string taskDescription,
            string? baseBranch = null,
            CancellationToken ct = default)
        {
            var workDir = AgentConfig.GetWorkDirectory();
            
            // Check if we're in a git repo
            if (!Directory.Exists(Path.Combine(workDir, ".git")))
            {
                throw new InvalidOperationException(
                    $"Work directory '{workDir}' is not a git repository. Run 'git init' first.");
            }

            var agentId = GenerateAgentId();
            var sanitizedTask = SanitizeBranchName(taskDescription);
            var branchName = $"agent/{agentId}/{sanitizedTask}";

            // Detect base branch if not specified
            baseBranch ??= await GetDefaultBranchAsync(workDir, ct) ?? "main";

            var session = new AgentSession
            {
                AgentId = agentId,
                TaskDescription = taskDescription,
                BranchName = branchName,
                BaseBranch = baseBranch,
                Status = "starting",
                StartedAt = DateTime.Now,
                WorkDirectory = workDir
            };

            // Stash any uncommitted changes
            await RunGitCommandAsync(workDir, "stash", ct);

            // Create and checkout new branch
            var createResult = await RunGitCommandAsync(
                workDir,
                $"checkout -b {branchName} {baseBranch}",
                ct);

            if (!createResult.Success)
            {
                // Try to restore stash if branch creation failed
                await RunGitCommandAsync(workDir, "stash pop", ct);
                throw new InvalidOperationException(
                    $"Failed to create branch '{branchName}': {createResult.Error}");
            }

            session.Status = "in_progress";
            
            lock (_lock)
            {
                _currentSession = session;
            }

            AgentLogger.LogInfo(
                "Started agent session {AgentId} on branch {Branch}",
                agentId, branchName);

            // Save session state
            await SaveSessionStateAsync(session, ct);

            return session;
        }

        /// <summary>
        /// Commit current changes with structured message
        /// </summary>
        public static async Task<bool> CommitAsync(
            string type,
            string message,
            CancellationToken ct = default)
        {
            var session = CurrentSession;
            if (session == null)
            {
                AgentLogger.LogWarning("No active session for commit");
                return false;
            }

            var workDir = session.WorkDirectory;

            // Stage all changes
            var addResult = await RunGitCommandAsync(workDir, "add -A", ct);
            if (!addResult.Success)
            {
                AgentLogger.LogError("Failed to stage changes: {Error}", addResult.Error);
                return false;
            }

            // Check if there are changes to commit
            var statusResult = await RunGitCommandAsync(workDir, "status --porcelain", ct);
            if (string.IsNullOrWhiteSpace(statusResult.Output))
            {
                AgentLogger.LogInfo("No changes to commit");
                return true;
            }

            // Format commit message
            var fullMessage = $"{type}: {message}\n\nAgent: {session.AgentId}\nTask: {session.TaskDescription}";

            // Commit
            var commitResult = await RunGitCommandAsync(
                workDir,
                $"commit -m \"{EscapeGitMessage(fullMessage)}\"",
                ct);

            if (!commitResult.Success)
            {
                AgentLogger.LogError("Failed to commit: {Error}", commitResult.Error);
                return false;
            }

            session.CommitCount++;
            AgentLogger.LogInfo("Committed: {Type}: {Message}", type, message);

            return true;
        }

        /// <summary>
        /// Create a checkpoint (tag) at current state
        /// </summary>
        public static async Task<AgentCheckpoint?> CreateCheckpointAsync(
            string? message = null,
            bool runTests = false,
            CancellationToken ct = default)
        {
            var session = CurrentSession;
            if (session == null)
            {
                AgentLogger.LogWarning("No active session for checkpoint");
                return null;
            }

            var workDir = session.WorkDirectory;
            var checkpointNum = session.Checkpoints.Count + 1;
            var tagName = $"{session.AgentId}/checkpoint-{checkpointNum}";

            // Get current commit SHA
            var shaResult = await RunGitCommandAsync(workDir, "rev-parse HEAD", ct);
            if (!shaResult.Success)
            {
                AgentLogger.LogError("Failed to get current commit: {Error}", shaResult.Error);
                return null;
            }

            var commitSha = shaResult.Output?.Trim() ?? "";

            // Run tests if requested
            bool testsPassed = true;
            if (runTests)
            {
                testsPassed = await RunTestsAsync(workDir, ct);
            }

            // Create tag
            var tagMessage = message ?? $"Checkpoint {checkpointNum}";
            var tagResult = await RunGitCommandAsync(
                workDir,
                $"tag -a {tagName} -m \"{EscapeGitMessage(tagMessage)}\"",
                ct);

            if (!tagResult.Success)
            {
                AgentLogger.LogError("Failed to create tag: {Error}", tagResult.Error);
                return null;
            }

            var checkpoint = new AgentCheckpoint
            {
                Tag = tagName,
                CommitSha = commitSha,
                Timestamp = DateTime.Now,
                Message = tagMessage,
                TestsPassed = testsPassed
            };

            session.Checkpoints.Add(checkpoint);
            await SaveSessionStateAsync(session, ct);

            AgentLogger.LogInfo(
                "Created checkpoint {Tag} at {Sha} (tests: {TestStatus})",
                tagName, commitSha.Substring(0, 7), testsPassed ? "passed" : "failed");

            return checkpoint;
        }

        /// <summary>
        /// Rollback to a checkpoint or commit
        /// </summary>
        public static async Task<bool> RollbackAsync(
            string? target = null,
            CancellationToken ct = default)
        {
            var session = CurrentSession;
            if (session == null)
            {
                AgentLogger.LogWarning("No active session for rollback");
                return false;
            }

            var workDir = session.WorkDirectory;

            // If no target specified, rollback to last checkpoint
            if (string.IsNullOrEmpty(target))
            {
                if (session.Checkpoints.Count == 0)
                {
                    AgentLogger.LogWarning("No checkpoints to rollback to");
                    return false;
                }
                target = session.Checkpoints[^1].Tag;
            }

            // Reset to target
            var resetResult = await RunGitCommandAsync(
                workDir,
                $"reset --hard {target}",
                ct);

            if (!resetResult.Success)
            {
                AgentLogger.LogError("Failed to rollback: {Error}", resetResult.Error);
                return false;
            }

            AgentLogger.LogInfo("Rolled back to {Target}", target);
            return true;
        }

        /// <summary>
        /// Complete the session and optionally merge to base branch
        /// </summary>
        public static async Task<bool> CompleteSessionAsync(
            bool merge = false,
            bool deleteBranch = false,
            CancellationToken ct = default)
        {
            var session = CurrentSession;
            if (session == null)
            {
                AgentLogger.LogWarning("No active session to complete");
                return false;
            }

            var workDir = session.WorkDirectory;

            if (merge)
            {
                // Checkout base branch
                var checkoutResult = await RunGitCommandAsync(
                    workDir,
                    $"checkout {session.BaseBranch}",
                    ct);

                if (!checkoutResult.Success)
                {
                    AgentLogger.LogError("Failed to checkout base branch: {Error}", checkoutResult.Error);
                    return false;
                }

                // Merge agent branch
                var mergeResult = await RunGitCommandAsync(
                    workDir,
                    $"merge {session.BranchName} --no-ff -m \"Merge {session.BranchName}: {session.TaskDescription}\"",
                    ct);

                if (!mergeResult.Success)
                {
                    AgentLogger.LogError("Failed to merge: {Error}", mergeResult.Error);
                    // Abort merge and go back to agent branch
                    await RunGitCommandAsync(workDir, "merge --abort", ct);
                    await RunGitCommandAsync(workDir, $"checkout {session.BranchName}", ct);
                    return false;
                }

                AgentLogger.LogInfo("Merged {Branch} to {Base}", session.BranchName, session.BaseBranch);

                // Delete agent branch if requested
                if (deleteBranch)
                {
                    await RunGitCommandAsync(workDir, $"branch -d {session.BranchName}", ct);
                    AgentLogger.LogInfo("Deleted branch {Branch}", session.BranchName);
                }
            }

            session.Status = "completed";
            session.CompletedAt = DateTime.Now;
            await SaveSessionStateAsync(session, ct);

            lock (_lock)
            {
                _currentSession = null;
            }

            return true;
        }

        /// <summary>
        /// Abort the session and return to base branch
        /// </summary>
        public static async Task<bool> AbortSessionAsync(
            bool deleteBranch = true,
            CancellationToken ct = default)
        {
            var session = CurrentSession;
            if (session == null)
            {
                return true; // Nothing to abort
            }

            var workDir = session.WorkDirectory;

            // Discard any uncommitted changes
            await RunGitCommandAsync(workDir, "reset --hard HEAD", ct);

            // Checkout base branch
            await RunGitCommandAsync(workDir, $"checkout {session.BaseBranch}", ct);

            // Restore stashed changes if any
            await RunGitCommandAsync(workDir, "stash pop", ct);

            // Delete agent branch if requested
            if (deleteBranch)
            {
                await RunGitCommandAsync(workDir, $"branch -D {session.BranchName}", ct);
            }

            session.Status = "aborted";
            session.CompletedAt = DateTime.Now;

            lock (_lock)
            {
                _currentSession = null;
            }

            AgentLogger.LogInfo("Aborted session {AgentId}", session.AgentId);
            return true;
        }

        /// <summary>
        /// Get current git status
        /// </summary>
        public static async Task<string> GetStatusAsync(CancellationToken ct = default)
        {
            var session = CurrentSession;
            var workDir = session?.WorkDirectory ?? AgentConfig.GetWorkDirectory();

            var branchResult = await RunGitCommandAsync(workDir, "branch --show-current", ct);
            var statusResult = await RunGitCommandAsync(workDir, "status --short", ct);

            var status = $"Branch: {branchResult.Output?.Trim() ?? "unknown"}\n";
            if (session != null)
            {
                status += $"Agent: {session.AgentId}\n";
                status += $"Task: {session.TaskDescription}\n";
                status += $"Commits: {session.CommitCount}\n";
                status += $"Checkpoints: {session.Checkpoints.Count}\n";
            }
            status += $"\nChanges:\n{statusResult.Output ?? "(none)"}";

            return status;
        }

        #region Helper Methods

        private static string SanitizeBranchName(string name)
        {
            // Convert to lowercase and replace invalid characters
            var sanitized = name.ToLowerInvariant()
                .Replace(" ", "-")
                .Replace("_", "-")
                .Replace("/", "-")
                .Replace("\\", "-")
                .Replace(":", "-")
                .Replace(".", "-");

            // Remove consecutive hyphens
            while (sanitized.Contains("--"))
                sanitized = sanitized.Replace("--", "-");

            // Trim to reasonable length
            if (sanitized.Length > 50)
                sanitized = sanitized.Substring(0, 50);

            return sanitized.Trim('-');
        }

        private static string EscapeGitMessage(string message)
        {
            return message
                .Replace("\"", "\\\"")
                .Replace("\n", "\" -m \"");
        }

        private static async Task<string?> GetDefaultBranchAsync(string workDir, CancellationToken ct)
        {
            // Try to get default branch from remote
            var result = await RunGitCommandAsync(
                workDir,
                "symbolic-ref refs/remotes/origin/HEAD --short",
                ct);

            if (result.Success && !string.IsNullOrWhiteSpace(result.Output))
            {
                return result.Output.Trim().Replace("origin/", "");
            }

            // Check if main or master exists
            var mainResult = await RunGitCommandAsync(workDir, "show-ref --verify refs/heads/main", ct);
            if (mainResult.Success)
                return "main";

            var masterResult = await RunGitCommandAsync(workDir, "show-ref --verify refs/heads/master", ct);
            if (masterResult.Success)
                return "master";

            return null;
        }

        private static async Task<bool> RunTestsAsync(string workDir, CancellationToken ct)
        {
            try
            {
                var result = await RunGitCommandAsync(
                    workDir,
                    "dotnet test --no-build --verbosity quiet",
                    ct,
                    timeoutMs: 300000); // 5 minutes

                return result.Success;
            }
            catch
            {
                return false;
            }
        }

        private static async Task SaveSessionStateAsync(AgentSession session, CancellationToken ct)
        {
            try
            {
                var stateFile = Path.Combine(session.WorkDirectory, ".thuvu-session.json");
                var json = JsonSerializer.Serialize(session, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(stateFile, json, ct);
            }
            catch (Exception ex)
            {
                AgentLogger.LogWarning("Failed to save session state: {Error}", ex.Message);
            }
        }

        private static async Task<(bool Success, string? Output, string? Error)> RunGitCommandAsync(
            string workDir,
            string arguments,
            CancellationToken ct,
            int timeoutMs = 30000)
        {
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "git",
                        Arguments = arguments,
                        WorkingDirectory = workDir,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();

                using var cts = new CancellationTokenSource(timeoutMs);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);

                var output = await process.StandardOutput.ReadToEndAsync(linkedCts.Token);
                var error = await process.StandardError.ReadToEndAsync(linkedCts.Token);

                await process.WaitForExitAsync(linkedCts.Token);

                return (process.ExitCode == 0, output, error);
            }
            catch (Exception ex)
            {
                return (false, null, ex.Message);
            }
        }

        #endregion

        #region Console Output

        /// <summary>
        /// Print session status to console
        /// </summary>
        public static void PrintSessionStatus()
        {
            var session = CurrentSession;
            if (session == null)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("No active agent session");
                Console.ResetColor();
                return;
            }

            var boxWidth = 60;
            var line = new string('─', boxWidth - 2);

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"┌{line}┐");
            Console.WriteLine($"│  Agent Session: {session.AgentId,-39} │");
            Console.WriteLine($"├{line}┤");
            Console.ResetColor();

            Console.Write("│  ");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write("Task:        ");
            Console.ResetColor();
            var task = session.TaskDescription.Length > 40
                ? session.TaskDescription.Substring(0, 37) + "..."
                : session.TaskDescription;
            Console.WriteLine($"{task,-41} │");

            Console.Write("│  ");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write("Branch:      ");
            Console.ForegroundColor = ConsoleColor.Green;
            var branch = session.BranchName.Length > 40
                ? "..." + session.BranchName.Substring(session.BranchName.Length - 37)
                : session.BranchName;
            Console.Write(branch);
            Console.ResetColor();
            var branchPadding = new string(' ', Math.Max(0, 41 - branch.Length));
            Console.WriteLine($"{branchPadding} │");

            Console.Write("│  ");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write("Status:      ");
            Console.ForegroundColor = session.Status == "in_progress" ? ConsoleColor.Green : ConsoleColor.Yellow;
            Console.Write(session.Status);
            Console.ResetColor();
            var statusPadding = new string(' ', Math.Max(0, 41 - session.Status.Length));
            Console.WriteLine($"{statusPadding} │");

            Console.Write("│  ");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write("Commits:     ");
            Console.ResetColor();
            Console.WriteLine($"{session.CommitCount,-41} │");

            Console.Write("│  ");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write("Checkpoints: ");
            Console.ResetColor();
            Console.WriteLine($"{session.Checkpoints.Count,-41} │");

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"└{line}┘");
            Console.ResetColor();
            Console.WriteLine();
        }

        #endregion
    }
}
