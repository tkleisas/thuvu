using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Threading;
using thuvu.Desktop.Services;
using thuvu.Models;

namespace thuvu.Desktop.ViewModels;

/// <summary>
/// ViewModel for a single chat message
/// </summary>
public partial class ChatMessageViewModel : ObservableObject
{
    [ObservableProperty] private string _role = string.Empty;
    [ObservableProperty] private string _content = string.Empty;
    [ObservableProperty] private string _timestamp = string.Empty;
    [ObservableProperty] private bool _isStreaming;
    [ObservableProperty] private string? _toolName;
    [ObservableProperty] private string? _toolArgs;
    [ObservableProperty] private string? _toolResult;
    [ObservableProperty] private string? _thinkingContent;
    [ObservableProperty] private bool _showThinking;
}

/// <summary>
/// Represents a model choice in the dropdown
/// </summary>
public class ModelChoice
{
    public string ModelId { get; set; } = "";
    public string DisplayLabel { get; set; } = "";
    public override string ToString() => DisplayLabel;
}

/// <summary>
/// ViewModel for the Chat dockable panel.
/// Each chat owns an independent agent session.
/// </summary>
public partial class ChatViewModel : DocumentViewModel
{
    [ObservableProperty] private string _inputText = string.Empty;
    [ObservableProperty] private bool _isProcessing;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendMessageCommand))]
    private bool _canSend = true;

    private DesktopAgentService? _agentService;

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = new();
    public ObservableCollection<ModelChoice> AvailableModels { get; } = new();

    [ObservableProperty] private ModelChoice? _selectedModel;

    /// <summary>The agent service powering this chat</summary>
    public DesktopAgentService? AgentService => _agentService;

    public ChatViewModel()
    {
        Id = "Chat";
        Title = "💬 Chat";
        CanClose = true;
    }

    private void LoadAvailableModels()
    {
        AvailableModels.Clear();
        var models = ModelRegistry.Instance.Models.Where(m => m.Enabled).ToList();

        // Fallback: if registry is empty, add the current model from AgentConfig
        if (models.Count == 0 && !string.IsNullOrEmpty(AgentConfig.Config.Model))
        {
            AvailableModels.Add(new ModelChoice
            {
                ModelId = AgentConfig.Config.Model,
                DisplayLabel = AgentConfig.Config.Model
            });
        }
        else
        {
            foreach (var ep in models)
            {
                AvailableModels.Add(new ModelChoice
                {
                    ModelId = ep.ModelId,
                    DisplayLabel = string.IsNullOrEmpty(ep.DisplayName) ? ep.ModelId : ep.DisplayName
                });
            }
        }

        // Select the effective model for this agent
        var effectiveId = _agentService?.EffectiveModel ?? ModelRegistry.Instance.DefaultModelId;
        SelectedModel = AvailableModels.FirstOrDefault(m => m.ModelId == effectiveId)
                        ?? AvailableModels.FirstOrDefault();
    }

    partial void OnSelectedModelChanged(ModelChoice? value)
    {
        if (value != null && _agentService != null)
        {
            _agentService.SetModel(value.ModelId);
        }
    }

    public void SetAgentService(DesktopAgentService service)
    {
        _agentService = service;
        LoadAvailableModels();
        _agentService.OnToken += token =>
            Dispatcher.UIThread.Post(() => AppendToLastAssistant(token));
        _agentService.OnReasoningToken += token =>
            Dispatcher.UIThread.Post(() => AppendThinking(token));
        _agentService.OnToolCall += (name, args) =>
            Dispatcher.UIThread.Post(() => AddToolMessage(name, args));
        _agentService.OnToolComplete += (name, args, result, elapsed) =>
            Dispatcher.UIThread.Post(() => UpdateToolResult(name, result, elapsed));
        _agentService.OnComplete += () =>
            Dispatcher.UIThread.Post(() => FinalizeResponse());
        _agentService.OnError += error =>
            Dispatcher.UIThread.Post(() => HandleError(error));
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendMessage()
    {
        if (string.IsNullOrWhiteSpace(InputText) || _agentService == null) return;
        
        var prompt = InputText;
        InputText = string.Empty;

        // Intercept slash commands before sending to LLM
        if (prompt.StartsWith("/"))
        {
            Messages.Add(new ChatMessageViewModel
            {
                Role = "user",
                Content = prompt,
                Timestamp = DateTime.Now.ToString("HH:mm:ss")
            });

            if (await TryHandleCommandAsync(prompt))
                return;
        }
        else
        {
            Messages.Add(new ChatMessageViewModel
            {
                Role = "user",
                Content = prompt,
                Timestamp = DateTime.Now.ToString("HH:mm:ss")
            });
        }

        IsProcessing = true;
        CanSend = false;

        // Streaming placeholder
        Messages.Add(new ChatMessageViewModel
        {
            Role = "assistant",
            Content = "",
            Timestamp = DateTime.Now.ToString("HH:mm:ss"),
            IsStreaming = true
        });

        await _agentService.SendMessageAsync(prompt);
    }

    [RelayCommand]
    private void CancelRequest()
    {
        _agentService?.CancelRequest();
        FinalizeResponse();
    }

    /// <summary>
    /// Handle slash commands locally instead of sending to LLM.
    /// Returns true if the command was handled.
    /// </summary>
    private async Task<bool> TryHandleCommandAsync(string input)
    {
        var trimmed = input.Trim();

        if (trimmed.Equals("/help", StringComparison.OrdinalIgnoreCase))
        {
            AddSystemMessage("**Available Commands**\n" +
                "/help — Show this help\n" +
                "/clear — Clear conversation\n" +
                "/system <text> — Set system prompt\n" +
                "/stream on|off — Toggle streaming\n" +
                "/diff [--staged] — Show git diff\n" +
                "/test [project] — Run dotnet tests\n" +
                "/run CMD [args] — Run whitelisted command\n" +
                "/commit \"msg\" — Commit with test gate\n" +
                "/push — Push to remote\n" +
                "/pull — Pull from remote\n" +
                "/models — List available models\n" +
                "/health — Check service health\n" +
                "/status — Session and token status\n" +
                "/config — View configuration");
            return true;
        }

        if (trimmed.StartsWith("/clear", StringComparison.OrdinalIgnoreCase))
        {
            _agentService!.ClearMessages();
            Messages.Clear();
            AddSystemMessage("Conversation cleared.");
            return true;
        }

        if (trimmed.StartsWith("/system ", StringComparison.OrdinalIgnoreCase))
        {
            var sys = trimmed[8..].Trim();
            if (string.IsNullOrWhiteSpace(sys))
            {
                AddSystemMessage("Usage: /system <text>");
                return true;
            }
            _agentService!.ClearMessages();
            // Re-add with new system prompt will happen on next message via the service
            AddSystemMessage($"System prompt updated to: {sys}");
            return true;
        }

        if (trimmed.StartsWith("/stream", StringComparison.OrdinalIgnoreCase))
        {
            var arg = trimmed.Length > 7 ? trimmed[7..].Trim() : "";
            if (string.Equals(arg, "on", StringComparison.OrdinalIgnoreCase))
                AgentConfig.Config.Stream = true;
            else if (string.Equals(arg, "off", StringComparison.OrdinalIgnoreCase))
                AgentConfig.Config.Stream = false;
            else
            {
                AddSystemMessage($"Streaming is {(AgentConfig.Config.Stream ? "ON" : "OFF")}. Usage: /stream on|off");
                return true;
            }
            AddSystemMessage($"Streaming is now {(AgentConfig.Config.Stream ? "ON" : "OFF")}.");
            return true;
        }

        if (trimmed.StartsWith("/diff", StringComparison.OrdinalIgnoreCase))
        {
            await RunToolCommandAsync("git_diff", "{}");
            return true;
        }

        if (trimmed.StartsWith("/test", StringComparison.OrdinalIgnoreCase))
        {
            await RunToolCommandAsync("dotnet_test", "{}");
            return true;
        }

        if (trimmed.StartsWith("/run ", StringComparison.OrdinalIgnoreCase))
        {
            var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                AddSystemMessage("Usage: /run CMD [args]");
                return true;
            }
            var cmd = parts[1];
            var args = parts.Length > 2 ? parts[2..] : Array.Empty<string>();
            var payload = JsonSerializer.Serialize(new { cmd, args, timeout_ms = 120000 });
            await RunToolCommandAsync("run_process", payload);
            return true;
        }

        if (trimmed.StartsWith("/commit", StringComparison.OrdinalIgnoreCase))
        {
            var msg = trimmed.Length > 8 ? trimmed[8..].Trim().Trim('"') : "";
            if (string.IsNullOrWhiteSpace(msg))
            {
                AddSystemMessage("Usage: /commit \"message\"");
                return true;
            }
            var payload = JsonSerializer.Serialize(new { message = msg });
            await RunToolCommandAsync("git_commit", payload);
            return true;
        }

        if (trimmed.StartsWith("/push", StringComparison.OrdinalIgnoreCase))
        {
            await RunToolCommandAsync("run_process", JsonSerializer.Serialize(new { cmd = "git", args = new[] { "push" }, timeout_ms = 60000 }));
            return true;
        }

        if (trimmed.StartsWith("/pull", StringComparison.OrdinalIgnoreCase))
        {
            await RunToolCommandAsync("run_process", JsonSerializer.Serialize(new { cmd = "git", args = new[] { "pull" }, timeout_ms = 60000 }));
            return true;
        }

        if (trimmed.StartsWith("/models", StringComparison.OrdinalIgnoreCase))
        {
            var models = ModelRegistry.Instance.Models.Where(m => m.Enabled).ToList();
            if (models.Count == 0)
            {
                AddSystemMessage($"Current model: {_agentService!.EffectiveModel}");
            }
            else
            {
                var lines = models.Select(m =>
                {
                    var current = m.ModelId == _agentService!.EffectiveModel ? " ← active" : "";
                    var name = string.IsNullOrEmpty(m.DisplayName) ? m.ModelId : $"{m.DisplayName} ({m.ModelId})";
                    return $"• {name}{current}";
                });
                AddSystemMessage("**Available Models**\n" + string.Join("\n", lines));
            }
            return true;
        }

        if (trimmed.Equals("/health", StringComparison.OrdinalIgnoreCase))
        {
            AddSystemMessage("Running health checks...");
            var report = await HealthCheck.RunAllChecksAsync(null!, CancellationToken.None);
            var sb = new System.Text.StringBuilder("**Health Check**\n");
            sb.AppendLine($"LM Studio: {(report.LmStudio ? "✅" : "❌")}");
            sb.AppendLine($"Git: {(report.Git ? "✅" : "❌")}");
            sb.AppendLine($"Work Directory: {(report.WorkDirectory ? "✅" : "❌")}");
            sb.AppendLine($"Deno: {(report.Deno ? "✅" : "❌")}");
            AddSystemMessage(sb.ToString());
            return true;
        }

        if (trimmed.Equals("/status", StringComparison.OrdinalIgnoreCase))
        {
            var model = _agentService!.EffectiveModel;
            var msgCount = _agentService.Messages.Count;
            AddSystemMessage($"**Status**\nModel: {model}\nMessages: {msgCount}\nStreaming: {AgentConfig.Config.Stream}");
            return true;
        }

        if (trimmed.Equals("/config", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("/config ", StringComparison.OrdinalIgnoreCase))
        {
            var sb = new System.Text.StringBuilder("**Configuration**\n");
            sb.AppendLine($"Host: {AgentConfig.Config.HostUrl}");
            sb.AppendLine($"Model: {AgentConfig.Config.Model}");
            sb.AppendLine($"Stream: {AgentConfig.Config.Stream}");
            sb.AppendLine($"Timeout: {AgentConfig.Config.TimeoutMs}ms");
            sb.AppendLine($"Work Dir: {AgentConfig.Config.WorkDirectory}");
            AddSystemMessage(sb.ToString());
            return true;
        }

        if (trimmed.StartsWith("/plan", StringComparison.OrdinalIgnoreCase))
        {
            await HandlePlanCommandAsync(trimmed);
            return true;
        }

        if (trimmed.StartsWith("/orchestrate", StringComparison.OrdinalIgnoreCase))
        {
            await HandleOrchestrateCommandAsync(trimmed);
            return true;
        }

        // Unknown slash command - don't send to LLM
        if (!trimmed.Contains(' ') || trimmed.Split(' ')[0].Length < 20)
        {
            AddSystemMessage($"Unknown command: {trimmed.Split(' ')[0]}. Type /help for available commands.");
            return true;
        }

        return false;
    }

    private string GetWorkDir() => _agentService?.WorkDirectory ?? AgentConfig.GetWorkDirectory();

    private string GetPlanPath(string? customPath = null)
    {
        var workDir = GetWorkDir();
        if (customPath != null)
            return Path.IsPathRooted(customPath) ? customPath : Path.Combine(workDir, customPath);
        return Path.Combine(workDir, "current-plan.json");
    }

    private async Task HandlePlanCommandAsync(string input)
    {
        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var subCmd = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";

        if (subCmd == "help" || subCmd == "")
        {
            AddSystemMessage("**Plan Command**\n" +
                "/plan <task description> — Analyze and decompose a task\n" +
                "/plan show — Show current plan\n" +
                "/plan load [file] — Load plan from file");
            return;
        }

        if (subCmd == "show")
        {
            var planPath = GetPlanPath();
            if (!File.Exists(planPath))
            {
                AddSystemMessage("No current plan. Use `/plan <description>` to create one.");
                return;
            }
            var plan = TaskPlan.LoadFromFile(planPath);
            if (plan == null) { AddSystemMessage("Failed to parse plan file."); return; }
            AddSystemMessage(FormatPlanSummary(plan));
            return;
        }

        if (subCmd == "load")
        {
            var file = parts.Length > 2 ? parts[2] : null;
            var planPath = GetPlanPath(file);
            if (!File.Exists(planPath)) { AddSystemMessage($"Plan file not found: {planPath}"); return; }
            var plan = TaskPlan.LoadFromFile(planPath);
            if (plan == null) { AddSystemMessage("Failed to parse plan file."); return; }
            AddSystemMessage(FormatPlanSummary(plan));
            return;
        }

        // Decompose task
        var taskDescription = input.Length > 5 ? input[5..].Trim() : "";
        if (string.IsNullOrWhiteSpace(taskDescription))
        {
            AddSystemMessage("Please provide a task description. Use `/plan help` for usage.");
            return;
        }

        AddSystemMessage("🔍 Analyzing task and creating decomposition plan...");

        try
        {
            // Get codebase context
            string? codebaseContext = null;
            var workDir = GetWorkDir();
            try
            {
                var files = Directory.GetFiles(workDir, "*.cs", SearchOption.AllDirectories)
                    .Take(20).Select(f => Path.GetRelativePath(workDir, f)).ToList();
                codebaseContext = files.Any()
                    ? $"Existing project files: {string.Join(", ", files.Take(10))}"
                    : "Work directory is empty - new project.";
            }
            catch { }

            var http = new HttpClient();
            AgentConfig.ApplyConfig(http);
            var decomposer = new TaskDecomposer(http);
            var plan = await decomposer.DecomposeAsync(taskDescription, codebaseContext, CancellationToken.None);

            var jsonPath = GetPlanPath();
            var mdPath = Path.ChangeExtension(jsonPath, ".md");
            plan.SaveToFile(jsonPath);
            plan.SaveToMarkdown(mdPath);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine(FormatPlanSummary(plan));
            sb.AppendLine();
            sb.AppendLine($"📁 Plan saved to: `{jsonPath}`");
            sb.AppendLine($"Use `/orchestrate` to execute this plan.");
            AddSystemMessage(sb.ToString());
        }
        catch (Exception ex)
        {
            AddSystemMessage($"⚠️ Failed to decompose task: {ex.Message}");
        }
    }

    private async Task HandleOrchestrateCommandAsync(string input)
    {
        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int? maxAgents = null;
        bool autoMerge = true;
        string? planFile = null;
        bool resetProgress = false;
        bool retryFailed = false;

        for (int i = 1; i < parts.Length; i++)
        {
            if (parts[i] == "--agents" && i + 1 < parts.Length && int.TryParse(parts[i + 1], out var n))
            { maxAgents = Math.Clamp(n, 1, 8); i++; }
            else if (parts[i] == "--no-merge") autoMerge = false;
            else if (parts[i] == "--plan" && i + 1 < parts.Length) planFile = parts[++i];
            else if (parts[i] == "--reset") resetProgress = true;
            else if (parts[i] == "--retry") retryFailed = true;
            else if (parts[i] == "help")
            {
                AddSystemMessage("**Orchestrate Command**\n" +
                    "/orchestrate — Execute current plan\n" +
                    "/orchestrate --agents N — Use N agents (1-8)\n" +
                    "/orchestrate --plan FILE — Use specific plan file\n" +
                    "/orchestrate --no-merge — Don't auto-merge branches\n" +
                    "/orchestrate --reset — Reset all tasks to pending\n" +
                    "/orchestrate --retry — Retry failed tasks only");
                return;
            }
        }

        var planPath = GetPlanPath(planFile);
        var workDir = GetWorkDir();

        if (!File.Exists(planPath))
        {
            AddSystemMessage($"No plan found at `{planPath}`. Use `/plan <description>` to create one.");
            return;
        }

        TaskPlan? plan;
        try
        {
            plan = TaskPlan.LoadFromFile(planPath);
            if (plan == null) { AddSystemMessage("Failed to load plan file."); return; }
        }
        catch (Exception ex) { AddSystemMessage($"⚠️ Error loading plan: {ex.Message}"); return; }

        // Handle reset/retry
        if (resetProgress)
        {
            foreach (var task in plan.SubTasks)
            {
                task.Status = SubTaskStatus.Pending;
                task.AssignedAgentId = null;
            }
            plan.SaveToFile(planPath);
            AddSystemMessage("Reset all task statuses to Pending.");
        }
        else if (retryFailed)
        {
            int resetCount = plan.ResetFailedTasks();
            plan.SaveToFile(planPath);
            AddSystemMessage(resetCount > 0
                ? $"Reset {resetCount} failed/blocked task(s) to Pending."
                : "No tasks to retry.");
        }

        var (pending, completed, failed, blocked, inProgress) = plan.GetStatusCounts();

        if (!plan.CanMakeProgress())
        {
            AddSystemMessage($"Cannot make progress: no tasks ready.\n" +
                $"Pending: {pending}, Completed: {completed}, Failed: {failed}, Blocked: {blocked}\n" +
                $"Use `--retry` or `--reset` to reset tasks.");
            return;
        }

        var config = new OrchestratorConfig
        {
            MaxAgents = maxAgents ?? plan.RecommendedAgentCount,
            AutoMergeResults = autoMerge,
            UseProcessIsolation = false
        };

        bool isResume = completed > 0 || failed > 0;
        AddSystemMessage($"🚀 {(isResume ? "Resuming" : "Starting")} orchestration with {config.MaxAgents} agent(s)...\n" +
            $"Plan: {plan.Summary}\nRemaining: {pending} tasks\nWork dir: `{workDir}`");

        var http = new HttpClient();
        AgentConfig.ApplyConfig(http);
        using var orchestrator = new TaskOrchestrator(http, config, workDir);

        orchestrator.OnAgentStarted += (agentId, taskId) =>
            Dispatcher.UIThread.Post(() =>
                AddSystemMessage($"[{agentId}] Starting task {taskId}..."));

        orchestrator.OnTaskCompleted += (agentId, result) =>
        {
            var task = plan.SubTasks.FirstOrDefault(t => t.Id == result.TaskId);
            if (task != null)
            {
                task.Status = result.Success ? SubTaskStatus.Completed : SubTaskStatus.Failed;
                task.AssignedAgentId = agentId;
                try { plan.SaveToFile(planPath); } catch { }
            }
            Dispatcher.UIThread.Post(() =>
            {
                var icon = result.Success ? "✅" : "❌";
                AddSystemMessage($"{icon} [{agentId}] Task {result.TaskId} ({result.Duration.TotalSeconds:F1}s)");
            });
        };

        orchestrator.OnPhaseCompleted += phase =>
            Dispatcher.UIThread.Post(() =>
                AddSystemMessage($"── {phase} completed ──"));

        try
        {
            var result = await orchestrator.ExecutePlanAsync(plan, CancellationToken.None);
            plan.SaveToFile(planPath);
            plan.SaveToMarkdown(Path.ChangeExtension(planPath, ".md"));

            AddSystemMessage(result.Success
                ? $"✅ Orchestration completed in {result.Duration.TotalMinutes:F1} minutes."
                : $"⚠️ Orchestration failed: {result.Error}");
        }
        catch (OperationCanceledException)
        {
            plan.SaveToFile(planPath);
            AddSystemMessage("Orchestration cancelled. Progress saved.");
        }
        catch (Exception ex)
        {
            AddSystemMessage($"⚠️ Orchestration failed: {ex.Message}");
        }
    }

    private static string FormatPlanSummary(TaskPlan plan)
    {
        var (pending, completed, failed, blocked, inProgress) = plan.GetStatusCounts();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"**Plan: {plan.Summary}**");
        sb.AppendLine($"Tasks: {plan.SubTasks.Count} | Agents: {plan.RecommendedAgentCount} | Est: {plan.TotalEstimatedMinutes} min");
        sb.AppendLine($"Status: ✅{completed} ⏳{pending} 🔄{inProgress} ❌{failed} 🚫{blocked}");
        sb.AppendLine();
        foreach (var task in plan.SubTasks)
        {
            var icon = task.Status switch
            {
                SubTaskStatus.Completed => "✅",
                SubTaskStatus.Failed => "❌",
                SubTaskStatus.InProgress => "🔄",
                SubTaskStatus.Blocked => "🚫",
                _ => "⬜"
            };
            sb.AppendLine($"{icon} **{task.Id}**: {task.Title}");
            if (!string.IsNullOrEmpty(task.Description))
                sb.AppendLine($"   {task.Description}");
        }
        return sb.ToString().TrimEnd();
    }

    private void AddSystemMessage(string content)
    {
        Messages.Add(new ChatMessageViewModel
        {
            Role = "system",
            Content = content,
            Timestamp = DateTime.Now.ToString("HH:mm:ss")
        });
    }

    private readonly System.Text.StringBuilder _commandOutput = new();

    private async Task RunToolCommandAsync(string toolName, string argsJson)
    {
        try
        {
            var result = await ToolExecutor.ExecuteToolAsync(toolName, argsJson, CancellationToken.None);
            // Try to extract stdout/stderr from JSON result
            try
            {
                using var doc = JsonDocument.Parse(result);
                var root = doc.RootElement;
                var sb = new System.Text.StringBuilder();
                if (root.TryGetProperty("stdout", out var stdout) && stdout.GetString() is string s && !string.IsNullOrWhiteSpace(s))
                    sb.AppendLine(s);
                if (root.TryGetProperty("stderr", out var stderr) && stderr.GetString() is string e && !string.IsNullOrWhiteSpace(e))
                    sb.AppendLine($"⚠️ {e}");
                if (root.TryGetProperty("diff", out var diff) && diff.GetString() is string d && !string.IsNullOrWhiteSpace(d))
                    sb.AppendLine(d);
                if (root.TryGetProperty("error", out var err) && err.GetString() is string er && !string.IsNullOrWhiteSpace(er))
                    sb.AppendLine($"⚠️ {er}");

                var output = sb.Length > 0 ? sb.ToString().TrimEnd() : result;
                AddSystemMessage(output);
            }
            catch
            {
                AddSystemMessage(result);
            }
        }
        catch (Exception ex)
        {
            AddSystemMessage($"⚠️ {ex.Message}");
        }
    }

    private void AppendToLastAssistant(string token)
    {
        var last = Messages.LastOrDefault(m => m.Role == "assistant");
        if (last != null) last.Content += token;
    }

    private void AppendThinking(string token)
    {
        var last = Messages.LastOrDefault(m => m.Role == "assistant");
        if (last != null) last.ThinkingContent = (last.ThinkingContent ?? "") + token;
    }

    private void AddToolMessage(string name, string args)
    {
        Messages.Add(new ChatMessageViewModel
        {
            Role = "tool",
            ToolName = name,
            ToolArgs = args,
            Timestamp = DateTime.Now.ToString("HH:mm:ss")
        });
    }

    private void UpdateToolResult(string name, string result, TimeSpan elapsed)
    {
        var toolMsg = Messages.LastOrDefault(m => m.Role == "tool" && m.ToolName == name);
        if (toolMsg != null)
        {
            toolMsg.ToolResult = $"[{elapsed.TotalSeconds:F1}s] {(result.Length > 500 ? result[..500] + "..." : result)}";
        }
    }

    private void FinalizeResponse()
    {
        var last = Messages.LastOrDefault(m => m.Role == "assistant");
        if (last != null) last.IsStreaming = false;
        IsProcessing = false;
        CanSend = true;
    }

    private void HandleError(string error)
    {
        var last = Messages.LastOrDefault(m => m.Role == "assistant");
        if (last != null)
        {
            last.Content += $"\n\n⚠️ Error: {error}";
            last.IsStreaming = false;
        }
        IsProcessing = false;
        CanSend = true;
    }
}
