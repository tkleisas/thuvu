using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CodingAgent;
using thuvu.Models;
using thuvu.Tools;

namespace thuvu
{
    /// <summary>
    /// Handlers for slash commands (/diff, /test, /run, /commit, /push, /pull, /config, /set, /rag, /mcp)
    /// </summary>
    public static class CommandHandlers
    {
        // /diff [--staged] [--context N] [--root PATH] [PATH ...]
        public static async Task HandleDiffCommandAsync(string line, CancellationToken ct, Action<string, bool>? outputCallback = null)
        {
            var parts = ConsoleHelpers.TokenizeArgs(line);
            bool staged = false;
            int? context = null;
            string? root = null;
            var paths = new List<string>();

            for (int i = 1; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p.Equals("--staged", StringComparison.OrdinalIgnoreCase)) { staged = true; continue; }
                if (p.Equals("--context", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Count && int.TryParse(parts[i + 1], out var c))
                { context = Math.Clamp(c, 0, 100); i++; continue; }
                if (p.Equals("--root", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Count)
                { root = parts[++i]; continue; }
                if (p.StartsWith("--"))
                {
                    var msg = $"Unknown option: {p}";
                    if (outputCallback != null) outputCallback(msg, true);
                    else Console.WriteLine(msg);
                    continue;
                }
                paths.Add(p);
            }

            var args = new { paths = paths.Count > 0 ? paths : null, staged, context = context ?? 3, root };
            var toolResult = await ToolExecutor.ExecuteToolAsync("git_diff", JsonSerializer.Serialize(args), ct);

            ConsoleHelpers.AutoPrettyPrinterCallback("git_diff", toolResult);

            using var doc = JsonDocument.Parse(toolResult);
            if (doc.RootElement.TryGetProperty("stdout", out var so) && !string.IsNullOrEmpty(so.GetString()))
            {
                var stdout = so.GetString()!;
                if (outputCallback != null) outputCallback(stdout, false);
                else Console.WriteLine(stdout);
            }
            if (doc.RootElement.TryGetProperty("stderr", out var se) && !string.IsNullOrEmpty(se.GetString()))
            {
                var stderr = se.GetString()!;
                if (outputCallback != null) outputCallback(stderr, true);
                else Console.Error.WriteLine(stderr);
            }
        }

        // /test [SOLUTION_OR_PROJECT] [--filter EXP] [--logger trx|console]
        public static async Task HandleTestCommandAsync(string line, CancellationToken ct, Action<string, bool>? outputCallback = null)
        {
            var parts = ConsoleHelpers.TokenizeArgs(line);
            string? slnOrProj = null;
            string? filter = null;
            string? logger = null;

            for (int i = 1; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p.Equals("--filter", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Count)
                { filter = parts[++i]; continue; }
                if (p.Equals("--logger", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Count)
                { logger = parts[++i]; continue; }
                if (p.StartsWith("--")) { Console.WriteLine($"Unknown option: {p}"); continue; }
                if (slnOrProj == null) slnOrProj = p; else Console.WriteLine($"Ignoring extra arg: {p}");
            }

            var args = new { solution_or_project = slnOrProj, filter, logger = string.IsNullOrWhiteSpace(logger) ? "trx" : logger };
            var toolResult = await ToolExecutor.ExecuteToolAsync("dotnet_test", JsonSerializer.Serialize(args), ct);

            ConsoleHelpers.AutoPrettyPrinterCallback("dotnet_test", toolResult);

            using var doc = JsonDocument.Parse(toolResult);
            var stdout = doc.RootElement.GetProperty("stdout").GetString() ?? "";
            var stderr = doc.RootElement.GetProperty("stderr").GetString() ?? "";
            if (!string.IsNullOrWhiteSpace(stdout)) Console.WriteLine(stdout);
            if (!string.IsNullOrWhiteSpace(stderr)) Console.Error.WriteLine(stderr);
        }

        // /run CMD [ARGS ...] [--cwd PATH] [--timeout MS]
        public static async Task HandleRunCommandAsync(string line, CancellationToken ct, Action<string, bool>? outputCallback = null)
        {
            var parts = ConsoleHelpers.TokenizeArgs(line);
            if (parts.Count < 2)
            {
                Console.WriteLine("Usage: /run CMD [ARGS ...] [--cwd PATH] [--timeout MS]");
                return;
            }

            var cmd = parts[1];
            var args = new List<string>();
            string? cwd = null;
            int? timeout = null;

            for (int i = 2; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p.Equals("--cwd", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Count) { cwd = parts[++i]; continue; }
                if (p.Equals("--timeout", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Count && int.TryParse(parts[++i], out var t)) { timeout = Math.Clamp(t, 1000, 600000); continue; }
                args.Add(p);
            }

            if (!RunProcessToolImpl.AllowedCmds.Contains(cmd))
            {
                Console.WriteLine($"Command '{cmd}' not allowed. Allowed: {string.Join(", ", RunProcessToolImpl.AllowedCmds)}");
                return;
            }

            var payload = new { cmd, args = args.ToArray(), cwd, timeout_ms = timeout ?? 120000 };
            var toolResult = await ToolExecutor.ExecuteToolAsync("run_process", JsonSerializer.Serialize(payload), ct);

            ConsoleHelpers.AutoPrettyPrinterCallback("run_process", toolResult);

            using var doc = JsonDocument.Parse(toolResult);
            var stdout = doc.RootElement.GetProperty("stdout").GetString() ?? "";
            var stderr = doc.RootElement.GetProperty("stderr").GetString() ?? "";
            if (!string.IsNullOrWhiteSpace(stdout)) Console.WriteLine(stdout);
            if (!string.IsNullOrWhiteSpace(stderr)) Console.Error.WriteLine(stderr);
        }

        // /commit "message" [--all] [--staged] [--no-test] [--allow-empty] [--root PATH]
        public static async Task HandleCommitCommandAsync(string line, CancellationToken ct)
        {
            var parts = ConsoleHelpers.TokenizeArgs(line);

            if (parts.Count < 2)
            {
                Console.WriteLine("Usage: /commit \"message\" [--all] [--staged] [--no-test] [--allow-empty] [--root PATH]");
                return;
            }

            bool stageAll = false;
            bool useStagedOnly = false;
            bool noTest = false;
            bool allowEmpty = false;
            string? root = null;

            var messageParts = new List<string>();
            for (int i = 1; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p.Equals("--all", StringComparison.OrdinalIgnoreCase)) { stageAll = true; continue; }
                if (p.Equals("--staged", StringComparison.OrdinalIgnoreCase)) { useStagedOnly = true; continue; }
                if (p.Equals("--no-test", StringComparison.OrdinalIgnoreCase)) { noTest = true; continue; }
                if (p.Equals("--allow-empty", StringComparison.OrdinalIgnoreCase)) { allowEmpty = true; continue; }
                if (p.Equals("--root", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Count)
                {
                    root = parts[++i]; continue;
                }
                messageParts.Add(p);
            }

            var message = string.Join(' ', messageParts).Trim('"', ' ').Trim();
            if (string.IsNullOrWhiteSpace(message))
            {
                Console.WriteLine("Commit message required. Example: /commit \"Refactor HttpClient wrapper\"");
                return;
            }

            var startDir = Directory.GetCurrentDirectory();
            var cwd = !string.IsNullOrWhiteSpace(root) ? Path.GetFullPath(root)
                    : SearchFilesToolImpl.DetectProjectRoot(startDir) ?? startDir;

            // 1) Run tests unless skipped
            if (!noTest)
            {
                Console.WriteLine("Running tests before commit...");
                var testPayload = new { cmd = "dotnet", args = new[] { "test", "--logger", "trx" }, cwd, timeout_ms = 600_000 };
                var testJson = await ToolExecutor.ExecuteToolAsync("run_process", JsonSerializer.Serialize(testPayload), ct);

                ConsoleHelpers.AutoPrettyPrinterCallback("dotnet_test", testJson);

                using (var doc = JsonDocument.Parse(testJson))
                {
                    var stdout = doc.RootElement.GetProperty("stdout").GetString() ?? "";
                    var summary = TestSummary.ParseDotnetTestStdout(stdout);
                    if (summary == null)
                    {
                        string? trx = TestSummary.TryFindTrxPathFromStdoutOrFS(stdout, cwd);
                        if (trx != null) summary = TestSummary.ParseTrxSummary(trx);
                    }

                    if (summary == null)
                    {
                        Console.WriteLine("Could not parse test results. Aborting commit. Use --no-test to override.");
                        return;
                    }
                    if (summary.Failed > 0)
                    {
                        Console.WriteLine("❌ Tests failed. Aborting commit. Use --no-test to override.");
                        return;
                    }
                }
            }

            // 2) Stage changes
            if (stageAll && !useStagedOnly)
            {
                Console.WriteLine("Staging all changes...");
                var addPayload = new { cmd = "git", args = new[] { "add", "-A" }, cwd };
                await ToolExecutor.ExecuteToolAsync("run_process", JsonSerializer.Serialize(addPayload), ct);
            }

            // 3) Ensure there is something to commit unless --allow-empty
            if (!allowEmpty)
            {
                var diffPayload = new { cmd = "git", args = new[] { "diff", "--staged", "--name-only" }, cwd };
                var diffJson = await ToolExecutor.ExecuteToolAsync("run_process", JsonSerializer.Serialize(diffPayload), ct);
                using var d = JsonDocument.Parse(diffJson);
                var stagedList = (d.RootElement.GetProperty("stdout").GetString() ?? "").Trim();
                if (string.IsNullOrEmpty(stagedList))
                {
                    Console.WriteLine("No staged changes to commit. Use --all to stage or --allow-empty for an empty commit.");
                    return;
                }
            }

            // 4) Commit
            var commitArgs = new List<string> { "commit", "-m", message };
            if (allowEmpty) commitArgs.Add("--allow-empty");

            Console.WriteLine("Committing...");
            var commitPayload = new { cmd = "git", args = commitArgs.ToArray(), cwd };
            var commitJson = await ToolExecutor.ExecuteToolAsync("run_process", JsonSerializer.Serialize(commitPayload), ct);

            using (var cdoc = JsonDocument.Parse(commitJson))
            {
                var ec = cdoc.RootElement.GetProperty("exit_code").GetInt32();
                var stdout = cdoc.RootElement.GetProperty("stdout").GetString() ?? "";
                var stderr = cdoc.RootElement.GetProperty("stderr").GetString() ?? "";

                if (ec != 0)
                {
                    Console.WriteLine("git commit failed:");
                    if (!string.IsNullOrWhiteSpace(stdout)) Console.WriteLine(stdout);
                    if (!string.IsNullOrWhiteSpace(stderr)) Console.Error.WriteLine(stderr);
                    return;
                }
            }

            // 5) Show short SHA
            var shaPayload = new { cmd = "git", args = new[] { "rev-parse", "--short", "HEAD" }, cwd };
            var shaJson = await ToolExecutor.ExecuteToolAsync("run_process", JsonSerializer.Serialize(shaPayload), ct);
            using (var sdoc = JsonDocument.Parse(shaJson))
            {
                var shortSha = (sdoc.RootElement.GetProperty("stdout").GetString() ?? "").Trim();
                if (!string.IsNullOrEmpty(shortSha))
                {
                    ConsoleHelpers.WithColor(ConsoleColor.Green, () => Console.WriteLine($"✅ Committed as {shortSha} — \"{message}\""));
                }
                else
                {
                    Console.WriteLine($"✅ Commit completed — \"{message}\"");
                }
            }
        }

        // /config show|path|reload|save
        public static Task HandleConfigCommandAsync(string line, HttpClient http, CancellationToken ct)
        {
            var rest = line.Length > 7 ? line[7..].Trim() : "show";
            switch (rest.ToLowerInvariant())
            {
                case "path":
                    Console.WriteLine(AgentConfig.GetConfigPath());
                    break;
                case "show":
                    Console.WriteLine($"HostUrl   : {AgentConfig.Config.HostUrl}");
                    Console.WriteLine($"Model     : {AgentConfig.Config.Model}");
                    Console.WriteLine($"Stream    : {AgentConfig.Config.Stream}");
                    Console.WriteLine($"TimeoutMs : {AgentConfig.Config.TimeoutMs}");
                    Console.WriteLine($"HttpRequestTimeout : {AgentConfig.Config.HttpRequestTimeout} minutes");
                    break;
                case "reload":
                    AgentConfig.LoadConfig();
                    AgentConfig.ApplyConfig(http);
                    Console.WriteLine("Config reloaded.");
                    break;
                case "save":
                    Console.WriteLine(AgentConfig.SaveConfig() ? "Config saved." : "Failed to save config.");
                    break;
                default:
                    Console.WriteLine("Usage: /config [show|path|reload|save]");
                    break;
            }
            return Task.CompletedTask;
        }

        // /set model <id> | /set host <url> | /set stream on|off | /set timeout <ms> | /set httptimeout <minutes>
        public static Task HandleSetCommandAsync(string line, HttpClient http, CancellationToken ct)
        {
            var args = ConsoleHelpers.TokenizeArgs(line);
            if (args.Count < 3)
            {
                Console.WriteLine("Usage: /set model <id> | /set host <url> | /set stream on|off | /set timeout <ms> | /set httptimeout <minutes>");
                return Task.CompletedTask;
            }

            var key = args[1].ToLowerInvariant();
            switch (key)
            {
                case "model":
                    {
                        var id = string.Join(' ', args.Skip(2));
                        if (string.IsNullOrWhiteSpace(id)) { Console.WriteLine("Model id required."); break; }
                        AgentConfig.Config.Model = id.Trim();
                        AgentConfig.SaveConfig();
                        Console.WriteLine($"Model set to: {AgentConfig.Config.Model}");
                        break;
                    }
                case "host":
                    {
                        var url = string.Join(' ', args.Skip(2));
                        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                        {
                            Console.WriteLine("Please provide a valid http(s) URL, e.g. http://127.0.0.1:1234");
                            break;
                        }
                        AgentConfig.Config.HostUrl = uri.ToString().TrimEnd('/');
                        AgentConfig.SaveConfig();
                        Console.WriteLine($"Host set to: {AgentConfig.Config.HostUrl}");
                        Console.WriteLine("Note: Restart the application for the host change to take effect.");
                        break;
                    }
                case "stream":
                    {
                        var v = args[2].ToLowerInvariant();
                        if (v is "on" or "off")
                        {
                            AgentConfig.Config.Stream = v == "on";
                            AgentConfig.SaveConfig();
                            Console.WriteLine($"Streaming is now {(AgentConfig.Config.Stream ? "ON" : "OFF")}.");
                        }
                        else Console.WriteLine("Usage: /set stream on|off");
                        break;
                    }
                case "timeout":
                    {
                        if (!int.TryParse(args[2], out var ms) || ms < 1000 || ms > 600_000)
                        {
                            Console.WriteLine("Timeout must be 1000..600000 ms.");
                            break;
                        }
                        AgentConfig.Config.TimeoutMs = ms;
                        AgentConfig.SaveConfig();
                        Console.WriteLine($"Default process timeout set to {AgentConfig.Config.TimeoutMs} ms.");
                        break;
                    }
                case "httptimeout":
                    {
                        if (!int.TryParse(args[2], out var minutes) || minutes < 1 || minutes > 120)
                        {
                            Console.WriteLine("HTTP timeout must be 1..120 minutes.");
                            break;
                        }
                        AgentConfig.Config.HttpRequestTimeout = minutes;
                        AgentConfig.SaveConfig();
                        Console.WriteLine($"HTTP request timeout set to {AgentConfig.Config.HttpRequestTimeout} minutes.");
                        Console.WriteLine("Note: Restart the application for the timeout change to take effect.");
                        break;
                    }
                default:
                    Console.WriteLine("Supported keys: model, host, stream, timeout, httptimeout");
                    break;
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// Extract TypeScript code block from assistant response and execute it
        /// </summary>
        public static async Task<string?> TryExecuteMcpCodeBlockAsync(string content, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(content)) return null;

            var codeBlockPattern = new Regex(
                @"```(?:typescript|ts|javascript|js)\s*\n([\s\S]*?)```",
                RegexOptions.IgnoreCase);

            var match = codeBlockPattern.Match(content);
            if (!match.Success) return null;

            var code = match.Groups[1].Value.Trim();
            if (string.IsNullOrEmpty(code)) return null;

            // Ask for user approval if configured
            if (McpConfig.Instance.RequireApproval)
            {
                Console.WriteLine();
                ConsoleHelpers.WithColor(ConsoleColor.Yellow, () =>
                {
                    Console.WriteLine("═══════════════════════════════════════════════════════════");
                    Console.WriteLine("  MCP Code Execution Request");
                    Console.WriteLine("═══════════════════════════════════════════════════════════");
                });
                Console.WriteLine();
                ConsoleHelpers.WithColor(ConsoleColor.Cyan, () => Console.WriteLine(code));
                Console.WriteLine();
                Console.WriteLine("Execute this code? [Y]es / [N]o / [A]lways (disable approval)");
                Console.Write("> ");

                var key = Console.ReadKey(intercept: true);
                Console.WriteLine(key.KeyChar);

                if (char.ToUpperInvariant(key.KeyChar) == 'A')
                {
                    McpConfig.Instance.RequireApproval = false;
                    McpConfig.SaveConfig();
                    Console.WriteLine("Approval disabled for this session.");
                }
                else if (char.ToUpperInvariant(key.KeyChar) != 'Y')
                {
                    Console.WriteLine("Execution cancelled.");
                    return "User cancelled code execution.";
                }
            }

            Console.WriteLine();
            ConsoleHelpers.WithColor(ConsoleColor.Blue, () => Console.WriteLine("Executing MCP code..."));

            using var executor = new McpCodeExecutor();
            var result = await executor.ExecuteAsync(code, ct);

            Console.WriteLine();
            if (result.Success)
            {
                ConsoleHelpers.WithColor(ConsoleColor.Green, () => Console.WriteLine($"✓ Execution completed in {result.Duration.TotalMilliseconds:F0}ms"));

                if (!string.IsNullOrEmpty(result.Result))
                {
                    Console.WriteLine();
                    Console.WriteLine("Result:");
                    ConsoleHelpers.WithColor(ConsoleColor.White, () => Console.WriteLine(result.Result));
                }
            }
            else
            {
                ConsoleHelpers.WithColor(ConsoleColor.Red, () => Console.WriteLine($"✗ Execution failed: {result.Error}"));
            }

            if (result.ToolCalls.Count > 0)
            {
                Console.WriteLine();
                ConsoleHelpers.WithColor(ConsoleColor.DarkGray, () =>
                {
                    Console.WriteLine($"Tool calls made: {result.ToolCalls.Count}");
                    foreach (var call in result.ToolCalls)
                    {
                        var icon = call.Success ? "✓" : "✗";
                        Console.WriteLine($"  {icon} {call.ToolName} ({call.Duration.TotalMilliseconds:F0}ms)");
                    }
                });
            }

            Console.WriteLine();

            return result.Success
                ? $"Execution successful. Result: {result.Result ?? "(no return value)"}"
                : $"Execution failed: {result.Error}";
        }
        
        /// <summary>
        /// /plan [task description] - Decompose a task into subtasks and estimate agent count
        /// /plan load [file] - Load an existing plan from file
        /// /plan show - Show the current plan
        /// </summary>
        public static async Task<string> HandlePlanCommandAsync(string line, HttpClient http, CancellationToken ct)
        {
            var parts = ConsoleHelpers.TokenizeArgs(line);
            var subCommand = parts.Count > 1 ? parts[1].ToLowerInvariant() : "";
            
            // Handle subcommands
            if (subCommand == "load")
            {
                var filePath = parts.Count > 2 ? parts[2] : TaskPlan.GetDefaultPlanPath();
                return LoadPlan(filePath);
            }
            
            if (subCommand == "show")
            {
                return ShowCurrentPlan();
            }
            
            if (subCommand == "help" || subCommand == "")
            {
                return @"Usage: /plan <task description>
       /plan load [file]    - Load plan from file (default: current-plan.json)
       /plan show           - Show the current loaded plan

Examples:
  /plan Create a REST API for user management with CRUD operations
  /plan Add unit tests for the Calculator class
  /plan load my-plan.json

This command analyzes a task and breaks it down into subtasks,
showing estimated time, complexity, and recommended number of agents.
The plan is saved to the work directory for later execution.";
            }
            
            // Otherwise treat everything after /plan as the task description
            var taskDescription = line.Length > 5 ? line[5..].Trim() : "";
            
            if (string.IsNullOrWhiteSpace(taskDescription))
            {
                return "Please provide a task description. Use '/plan help' for usage.";
            }
            
            Console.WriteLine();
            ConsoleHelpers.WithColor(ConsoleColor.Cyan, () => 
                Console.WriteLine("🔍 Analyzing task and creating decomposition plan..."));
            Console.WriteLine();
            
            try
            {
                // Get codebase context from work directory (not thuvu source!)
                string? codebaseContext = null;
                var workDir = AgentConfig.GetWorkDirectory();
                
                try
                {
                    // Search in work directory only
                    var files = Directory.GetFiles(workDir, "*.cs", SearchOption.AllDirectories)
                        .Take(20)
                        .Select(f => Path.GetRelativePath(workDir, f))
                        .ToList();
                    
                    if (files.Any())
                    {
                        codebaseContext = $"Existing project files in work directory: {string.Join(", ", files.Take(10))}";
                    }
                    else
                    {
                        codebaseContext = "Work directory is empty - this will be a new project.";
                    }
                }
                catch { /* Ignore context errors */ }
                
                var decomposer = new TaskDecomposer(http);
                var plan = await decomposer.DecomposeAsync(taskDescription, codebaseContext, ct);
                
                // Save plan to files
                var jsonPath = TaskPlan.GetDefaultPlanPath();
                var mdPath = Path.ChangeExtension(jsonPath, ".md");
                
                plan.SaveToFile(jsonPath);
                plan.SaveToMarkdown(mdPath);
                
                // Print the plan
                TaskPlanPrinter.PrintPlan(plan);
                
                Console.WriteLine();
                ConsoleHelpers.WithColor(ConsoleColor.Green, () =>
                {
                    Console.WriteLine($"📁 Plan saved to:");
                    Console.WriteLine($"   JSON: {jsonPath}");
                    Console.WriteLine($"   Markdown: {mdPath}");
                });
                
                // Return summary
                return $"Task decomposed into {plan.SubTasks.Count} subtasks. " +
                       $"Recommended agents: {plan.RecommendedAgentCount}. " +
                       $"Estimated time: {plan.TotalEstimatedMinutes} minutes.\n" +
                       $"Use '/orchestrate' to execute this plan.";
            }
            catch (Exception ex)
            {
                ConsoleHelpers.WithColor(ConsoleColor.Red, () => 
                    Console.WriteLine($"✗ Failed to decompose task: {ex.Message}"));
                return $"Error: {ex.Message}";
            }
        }
        
        private static string LoadPlan(string filePath)
        {
            // If relative path, look in current directory
            if (!Path.IsPathRooted(filePath))
            {
                filePath = Path.Combine(Directory.GetCurrentDirectory(), filePath);
            }
            
            if (!File.Exists(filePath))
            {
                return $"Plan file not found: {filePath}";
            }
            
            try
            {
                var plan = TaskPlan.LoadFromFile(filePath);
                if (plan == null)
                {
                    return "Failed to parse plan file.";
                }
                
                TaskPlanPrinter.PrintPlan(plan);
                
                return $"Plan loaded: {plan.SubTasks.Count} subtasks, " +
                       $"recommended {plan.RecommendedAgentCount} agents.";
            }
            catch (Exception ex)
            {
                return $"Error loading plan: {ex.Message}";
            }
        }
        
        private static string ShowCurrentPlan()
        {
            var planPath = TaskPlan.GetDefaultPlanPath();
            
            if (!File.Exists(planPath))
            {
                return "No current plan. Use '/plan <description>' to create one.";
            }
            
            return LoadPlan(planPath);
        }
        
        /// <summary>
        /// /orchestrate [--agents N] [--no-merge] [--plan file] [--reset] - Execute a plan with multiple agents
        /// </summary>
        public static async Task<string> HandleOrchestrateCommandAsync(string line, HttpClient http, CancellationToken ct)
        {
            // Parse options
            var parts = ConsoleHelpers.TokenizeArgs(line);
            int? maxAgents = null;
            bool autoMerge = true;
            string? planFile = null;
            bool resetProgress = false;
            
            bool retryFailed = false;
            
            for (int i = 1; i < parts.Count; i++)
            {
                if (parts[i] == "--agents" && i + 1 < parts.Count && int.TryParse(parts[i + 1], out var n))
                {
                    maxAgents = Math.Clamp(n, 1, 8);
                    i++;
                }
                else if (parts[i] == "--no-merge")
                {
                    autoMerge = false;
                }
                else if (parts[i] == "--plan" && i + 1 < parts.Count)
                {
                    planFile = parts[++i];
                }
                else if (parts[i] == "--reset")
                {
                    resetProgress = true;
                }
                else if (parts[i] == "--retry")
                {
                    retryFailed = true;
                }
                else if (parts[i] == "help")
                {
                    return @"Usage: /orchestrate [options]

Options:
  --agents N     Number of agents to use (1-8, default: plan recommendation)
  --no-merge     Don't auto-merge agent branches
  --plan FILE    Use specific plan file (default: current-plan.json)
  --reset        Reset all task statuses to pending (start fresh)
  --retry        Reset only failed/blocked tasks to pending (retry failures)

The orchestrator reads the plan and executes subtasks. If tasks are already
marked as completed, they will be skipped (resume mode). Use --retry to retry
failed tasks, or --reset to start completely over.
Progress is saved after each task completes.";
                }
            }
            
            // Load plan from file (in current directory, not work directory)
            var currentDir = Directory.GetCurrentDirectory();
            var planPath = planFile != null 
                ? (Path.IsPathRooted(planFile) ? planFile : Path.Combine(currentDir, planFile))
                : TaskPlan.GetDefaultPlanPath();
            
            // Work directory is where agents will create project files
            var workDir = AgentConfig.GetWorkDirectory();
            
            if (!File.Exists(planPath))
            {
                return $"No plan found at {planPath}. Use '/plan <description>' to create one.";
            }
            
            TaskPlan? plan;
            try
            {
                plan = TaskPlan.LoadFromFile(planPath);
                if (plan == null)
                {
                    return "Failed to load plan file.";
                }
            }
            catch (Exception ex)
            {
                return $"Error loading plan: {ex.Message}";
            }
            
            // Handle reset option
            if (resetProgress)
            {
                foreach (var task in plan.SubTasks)
                {
                    task.Status = SubTaskStatus.Pending;
                    task.AssignedAgentId = null;
                }
                plan.SaveToFile(planPath);
                ConsoleHelpers.WithColor(ConsoleColor.Yellow, () =>
                    Console.WriteLine("Reset all task statuses to Pending."));
            }
            // Handle retry option (reset only failed/blocked/interrupted)
            else if (retryFailed)
            {
                var (pendingBefore, completedBefore, failedBefore, blockedBefore, inProgressBefore) = plan.GetStatusCounts();
                ConsoleHelpers.WithColor(ConsoleColor.Gray, () =>
                    Console.WriteLine($"Before retry: Pending={pendingBefore}, InProgress={inProgressBefore}, Completed={completedBefore}, Failed={failedBefore}, Blocked={blockedBefore}"));
                
                int resetCount = plan.ResetFailedTasks();
                plan.SaveToFile(planPath); // Always save after retry attempt
                
                if (resetCount > 0)
                {
                    ConsoleHelpers.WithColor(ConsoleColor.Yellow, () =>
                        Console.WriteLine($"Reset {resetCount} failed/blocked/interrupted task(s) to Pending."));
                }
                else
                {
                    ConsoleHelpers.WithColor(ConsoleColor.Yellow, () =>
                        Console.WriteLine("No tasks to retry (all tasks are either Pending or Completed)."));
                }
            }
            
            // Check for resume scenario
            var (pending, completed, failed, blocked, inProgress) = plan.GetStatusCounts();
            
            // Check if orchestration can make progress
            if (!plan.CanMakeProgress())
            {
                return $"Cannot make progress: no tasks are ready to run.\n" +
                       $"  Pending: {pending}, InProgress: {inProgress}, Completed: {completed}, Failed: {failed}, Blocked: {blocked}\n" +
                       $"  Use '--retry' to reset failed/blocked/interrupted tasks, or '--reset' to start over.";
            }
            
            bool isResume = completed > 0 || failed > 0;
            
            var config = new OrchestratorConfig
            {
                MaxAgents = maxAgents ?? plan.RecommendedAgentCount,
                AutoMergeResults = autoMerge,
                UseProcessIsolation = false // In-process for now
            };
            
            // Create progress tracking file in current directory (alongside plan)
            var progressPath = Path.Combine(currentDir, "orchestration-progress.json");
            
            Console.WriteLine();
            if (isResume)
            {
                ConsoleHelpers.WithColor(ConsoleColor.Yellow, () =>
                {
                    Console.WriteLine($"📂 Resuming orchestration...");
                    Console.WriteLine($"   Completed: {completed}, Failed: {failed}, Pending: {pending}, Blocked: {blocked}");
                });
            }
            ConsoleHelpers.WithColor(ConsoleColor.Cyan, () =>
            {
                Console.WriteLine($"🚀 {(isResume ? "Continuing" : "Starting")} orchestration with {config.MaxAgents} agent(s)...");
                Console.WriteLine($"   Plan: {plan.Summary}");
                Console.WriteLine($"   Remaining subtasks: {pending}");
                Console.WriteLine($"   Work directory: {workDir}");
            });
            Console.WriteLine();
            
            using var orchestrator = new TaskOrchestrator(http, config, workDir);
            
            // Set up event handlers for progress
            orchestrator.OnAgentStarted += (agentId, taskId) =>
            {
                ConsoleHelpers.WithColor(ConsoleColor.DarkGray, () =>
                    Console.WriteLine($"  [{agentId}] Starting task {taskId}..."));
            };
            
            orchestrator.OnTaskCompleted += (agentId, result) =>
            {
                var color = result.Success ? ConsoleColor.Green : ConsoleColor.Red;
                var icon = result.Success ? "✓" : "✗";
                ConsoleHelpers.WithColor(color, () =>
                    Console.WriteLine($"  [{agentId}] {icon} Task {result.TaskId} ({result.Duration.TotalSeconds:F1}s)"));
                
                // Update plan file with progress
                var task = plan.SubTasks.FirstOrDefault(t => t.Id == result.TaskId);
                if (task != null)
                {
                    task.Status = result.Success ? SubTaskStatus.Completed : SubTaskStatus.Failed;
                    task.AssignedAgentId = agentId;
                    try
                    {
                        plan.SaveToFile(planPath);
                        plan.SaveToMarkdown(Path.ChangeExtension(planPath, ".md"));
                    }
                    catch { /* Ignore save errors during progress */ }
                }
            };
            
            orchestrator.OnPhaseCompleted += (phase) =>
            {
                ConsoleHelpers.WithColor(ConsoleColor.Yellow, () =>
                    Console.WriteLine($"  ── {phase} completed ──"));
            };
            
            try
            {
                var result = await orchestrator.ExecutePlanAsync(plan, ct);
                
                // Save final state
                plan.SaveToFile(planPath);
                plan.SaveToMarkdown(Path.ChangeExtension(planPath, ".md"));
                
                // Save orchestration result
                SaveOrchestrationResult(result, progressPath);
                
                // Print result
                OrchestratorPrinter.PrintResult(result);
                
                return result.Success 
                    ? $"Orchestration completed successfully in {result.Duration.TotalMinutes:F1} minutes."
                    : $"Orchestration failed: {result.Error}";
            }
            catch (OperationCanceledException)
            {
                // Save progress before exiting
                plan.SaveToFile(planPath);
                return "Orchestration cancelled. Progress saved.";
            }
            catch (Exception ex)
            {
                ConsoleHelpers.WithColor(ConsoleColor.Red, () =>
                    Console.WriteLine($"✗ Orchestration failed: {ex.Message}"));
                return $"Error: {ex.Message}";
            }
        }
        
        private static void SaveOrchestrationResult(OrchestratorResult result, string filePath)
        {
            try
            {
                var json = JsonSerializer.Serialize(result, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
                File.WriteAllText(filePath, json);
            }
            catch { /* Ignore save errors */ }
        }
    }
}
