using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using Avalonia.Media.Imaging;
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
    [ObservableProperty] private bool _markdownFailed;

    /// <summary>
    /// True when streaming is done and markdown should be rendered.
    /// Falls back to plain text if markdown rendering produced empty output.
    /// </summary>
    public bool ShowMarkdown => Role == "assistant" && !IsStreaming && !MarkdownFailed;

    /// <summary>Show plain text while streaming OR as fallback when markdown fails</summary>
    public bool ShowPlainText => Role == "assistant" && (IsStreaming || MarkdownFailed);

    partial void OnIsStreamingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowMarkdown));
        OnPropertyChanged(nameof(ShowPlainText));
    }

    partial void OnMarkdownFailedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowMarkdown));
        OnPropertyChanged(nameof(ShowPlainText));
    }

    /// <summary>Images included in this message (for display in chat history)</summary>
    public ObservableCollection<ImageData> Images { get; } = new();

    public bool HasImages => Images.Count > 0;
}

/// <summary>Holds image data for display and API submission</summary>
public class ImageData
{
    public string Base64 { get; set; } = "";
    public string MimeType { get; set; } = "image/png";
    public Bitmap? Thumbnail { get; set; }
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

    // Context usage tracking
    [ObservableProperty] private int _promptTokens;
    [ObservableProperty] private int _completionTokens;
    [ObservableProperty] private int _totalTokens;
    [ObservableProperty] private int _maxContextTokens;
    [ObservableProperty] private double _contextUsagePercent;
    [ObservableProperty] private string _contextUsageText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendMessageCommand))]
    private bool _canSend = true;

    private CancellationTokenSource? _orchestrationCts;
    private DesktopAgentService? _agentService;

    /// <summary>Raised when an orchestration sub-agent changes state (agentId, status)</summary>
    public event Action<string, string>? OrchestrationAgentChanged;

    /// <summary>Display name for this session (used in session index)</summary>
    public string SessionName { get; set; } = "Chat";

    /// <summary>When this session was first created</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = new();
    public ObservableCollection<ModelChoice> AvailableModels { get; } = new();

    /// <summary>Images staged for the next message (preview strip)</summary>
    public ObservableCollection<ImageData> PendingImages { get; } = new();

    public bool HasPendingImages => PendingImages.Count > 0;

    [ObservableProperty] private ModelChoice? _selectedModel;

    /// <summary>The agent service powering this chat</summary>
    public DesktopAgentService? AgentService => _agentService;

    public ChatViewModel()
    {
        Id = "Chat";
        Title = "💬 Chat";
        CanClose = true;
        PendingImages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasPendingImages));
    }

    /// <summary>Add an image from raw bytes (clipboard paste, file picker)</summary>
    public void AddPendingImage(byte[] imageBytes, string mimeType = "image/png")
    {
        var base64 = Convert.ToBase64String(imageBytes);
        using var ms = new MemoryStream(imageBytes);
        var bmp = new Bitmap(ms);
        PendingImages.Add(new ImageData { Base64 = base64, MimeType = mimeType, Thumbnail = bmp });
    }

    /// <summary>Remove a pending image by index</summary>
    public void RemovePendingImage(int index)
    {
        if (index >= 0 && index < PendingImages.Count)
            PendingImages.RemoveAt(index);
    }

    public void ClearPendingImages() => PendingImages.Clear();

    public void RefreshModels()
    {
        // Reload config from disk so newly added models are picked up
        AgentConfig.LoadConfig();
        LoadAvailableModels();
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
        _agentService.OnContentReplace += content =>
            Dispatcher.UIThread.Post(() => ReplaceLastAssistantContent(content));
        _agentService.OnComplete += () =>
            Dispatcher.UIThread.Post(() => FinalizeResponse());
        _agentService.OnError += error =>
            Dispatcher.UIThread.Post(() => HandleError(error));
        _agentService.OnUsage += usage =>
            Dispatcher.UIThread.Post(() => UpdateContextUsage(usage));
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendMessage()
    {
        if ((string.IsNullOrWhiteSpace(InputText) && PendingImages.Count == 0) || _agentService == null) return;
        
        var prompt = InputText;
        InputText = string.Empty;

        // Snapshot pending images and clear the preview
        var images = PendingImages.ToList();
        PendingImages.Clear();

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
            var userMsg = new ChatMessageViewModel
            {
                Role = "user",
                Content = prompt,
                Timestamp = DateTime.Now.ToString("HH:mm:ss")
            };
            foreach (var img in images)
                userMsg.Images.Add(img);
            Messages.Add(userMsg);
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

        if (images.Count > 0)
            await _agentService.SendMessageWithImagesAsync(prompt, images);
        else
            await _agentService.SendMessageAsync(prompt);
    }

    [RelayCommand]
    private void CancelRequest()
    {
        _agentService?.CancelRequest();
        _orchestrationCts?.Cancel();
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
            var plan = await TaskPlan.LoadFromFileAsync(planPath);
            if (plan == null) { AddSystemMessage("Failed to parse plan file."); return; }
            AddSystemMessage(FormatPlanSummary(plan));
            return;
        }

        if (subCmd == "load")
        {
            var file = parts.Length > 2 ? parts[2] : null;
            var planPath = GetPlanPath(file);
            if (!File.Exists(planPath)) { AddSystemMessage($"Plan file not found: {planPath}"); return; }
            var plan = await TaskPlan.LoadFromFileAsync(planPath);
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
        IsProcessing = true;
        CanSend = false;

        try
        {
            var workDir = GetWorkDir();
            var jsonPath = GetPlanPath();

            var (summary, error) = await Task.Run(async () =>
            {
                try
                {
                    string? codebaseContext = null;
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

                    var mdPath = Path.ChangeExtension(jsonPath, ".md");
                    plan.SaveToFile(jsonPath);
                    plan.SaveToMarkdown(mdPath);

                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine(FormatPlanSummary(plan));
                    sb.AppendLine();
                    sb.AppendLine($"📁 Plan saved to: `{jsonPath}`");
                    sb.AppendLine($"Use `/orchestrate` to execute this plan.");
                    return (sb.ToString(), (string?)null);
                }
                catch (Exception ex)
                {
                    return ((string?)null, ex.Message);
                }
            });

            if (error != null)
                AddSystemMessage($"⚠️ Failed to decompose task: {error}");
            else
                AddSystemMessage(summary!);
        }
        finally
        {
            IsProcessing = false;
            CanSend = true;
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
            // Use async version to avoid deadlock (LoadFromFile uses .GetAwaiter().GetResult()
            // which deadlocks on UI thread due to SemaphoreSlim + SynchronizationContext)
            plan = await TaskPlan.LoadFromFileAsync(planPath);
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
            await plan.SaveToFileAsync(planPath);
            AddSystemMessage("Reset all task statuses to Pending.");
        }
        else if (retryFailed)
        {
            int resetCount = plan.ResetFailedTasks();
            await plan.SaveToFileAsync(planPath);
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

        // Show subtask overview
        var taskList = string.Join("\n", plan.SubTasks.Select(t =>
        {
            var icon = t.Status switch
            {
                SubTaskStatus.Completed => "✅",
                SubTaskStatus.Failed => "❌",
                SubTaskStatus.Blocked => "🚫",
                SubTaskStatus.InProgress => "⏳",
                _ => "⬜"
            };
            return $"  {icon} {t.Id}: {t.Title}";
        }));
        AddSystemMessage($"📋 Tasks:\n{taskList}");

        IsProcessing = true;
        CanSend = false;
        _orchestrationCts = new CancellationTokenSource();
        var ct = _orchestrationCts.Token;

        try
        {
            await Task.Run(async () =>
            {
                var http = new HttpClient();
                AgentConfig.ApplyConfig(http);
                var orchestrator = new TaskOrchestrator(http, config, workDir);

                try
                {
                    // Track active orchestration agents for UI
                    var activeAgents = new HashSet<string>();

                    orchestrator.OnAgentStarted += (agentId, taskId) =>
                    {
                        var taskTitle = plan.SubTasks.FirstOrDefault(t => t.Id == taskId)?.Title ?? taskId;
                        Dispatcher.UIThread.Post(() =>
                        {
                            AddSystemMessage($"🔧 [{agentId}] Starting: {taskTitle}");
                            if (activeAgents.Add(agentId))
                                OrchestrationAgentChanged?.Invoke(agentId, "Processing");
                            else
                                OrchestrationAgentChanged?.Invoke(agentId, "Processing");
                        });
                    };

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
                            var taskTitle = task?.Title ?? result.TaskId;
                            AddSystemMessage($"{icon} [{agentId}] {taskTitle} ({result.Duration.TotalSeconds:F1}s)");
                            OrchestrationAgentChanged?.Invoke(agentId, result.Success ? "Done" : "Failed");
                        });
                    };

                    orchestrator.OnPhaseCompleted += phase =>
                        Dispatcher.UIThread.Post(() =>
                            AddSystemMessage($"── {phase} ──"));

                    orchestrator.OnAgentToolCall += (agentId, toolName, status) =>
                        Dispatcher.UIThread.Post(() =>
                            OrchestrationAgentChanged?.Invoke(agentId, $"🔨 {toolName}"));

                    orchestrator.OnAgentOutput += (agentId, token) =>
                        Dispatcher.UIThread.Post(() =>
                        {
                            // Append streaming tokens to the last message from this agent
                            var last = Messages.LastOrDefault();
                            if (last?.Role == "system" && last.Content?.StartsWith($"[{agentId}]") == true
                                && !last.Content.Contains("Starting:") && !last.Content.Contains("completed"))
                            {
                                last.Content += token;
                            }
                        });

                    var result = await orchestrator.ExecutePlanAsync(plan, ct).ConfigureAwait(false);
                    plan.SaveToFile(planPath);
                    plan.SaveToMarkdown(Path.ChangeExtension(planPath, ".md"));

                    // Build final summary
                    var summary = new System.Text.StringBuilder();
                    if (result.Success)
                        summary.AppendLine($"✅ Orchestration completed in {result.Duration.TotalMinutes:F1} minutes.");
                    else
                        summary.AppendLine($"⚠️ Orchestration finished with issues: {result.Error}");

                    var (p2, c2, f2, b2, ip2) = plan.GetStatusCounts();
                    summary.AppendLine($"📊 Results: {c2} completed, {f2} failed, {b2} blocked, {p2} pending");

                    Dispatcher.UIThread.Post(() =>
                    {
                        AddSystemMessage(summary.ToString().TrimEnd());
                        // Remove orchestration agents from panel
                        foreach (var aid in activeAgents)
                            OrchestrationAgentChanged?.Invoke(aid, "Remove");
                    });
                }
                finally
                {
                    try { orchestrator.Dispose(); } catch { }
                    http.Dispose();
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { plan.SaveToFile(planPath); } catch { }
            Dispatcher.UIThread.Post(() =>
                AddSystemMessage("Orchestration cancelled. Progress saved."));
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
                AddSystemMessage($"⚠️ Orchestration failed: {ex.Message}"));
        }
        finally
        {
            _orchestrationCts?.Dispose();
            _orchestrationCts = null;
            Dispatcher.UIThread.Post(() =>
            {
                IsProcessing = false;
                CanSend = true;
            });
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
        var last = Messages.LastOrDefault();
        // If the last message is not an active streaming assistant, create a new one
        // This happens after tool calls complete — the next text chunk gets its own bubble
        if (last == null || last.Role != "assistant" || !last.IsStreaming)
        {
            Messages.Add(new ChatMessageViewModel
            {
                Role = "assistant",
                Content = token,
                Timestamp = DateTime.Now.ToString("HH:mm:ss"),
                IsStreaming = true
            });
            return;
        }
        last.Content += token;
    }

    /// <summary>Replace the last streaming assistant message content (used when inline tool text is stripped)</summary>
    private void ReplaceLastAssistantContent(string content)
    {
        var last = Messages.LastOrDefault(m => m.Role == "assistant" && m.IsStreaming)
                   ?? Messages.LastOrDefault(m => m.Role == "assistant");
        if (last != null) last.Content = content;
    }

    private void AppendThinking(string token)
    {
        // Find the last streaming assistant message (may not be the very last message if tools are between)
        var last = Messages.LastOrDefault(m => m.Role == "assistant" && m.IsStreaming);
        if (last == null)
        {
            // Create a new assistant bubble for thinking that arrives before content
            last = new ChatMessageViewModel
            {
                Role = "assistant",
                Content = "",
                Timestamp = DateTime.Now.ToString("HH:mm:ss"),
                IsStreaming = true
            };
            Messages.Add(last);
        }
        last.ThinkingContent = (last.ThinkingContent ?? "") + token;
    }

    private void AddToolMessage(string name, string args)
    {
        // Finalize the current streaming assistant message before inserting the tool bubble
        var lastAssistant = Messages.LastOrDefault(m => m.Role == "assistant" && m.IsStreaming);
        if (lastAssistant != null)
            lastAssistant.IsStreaming = false;

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
        // Find the last tool message with this name that has no result yet
        var toolMsg = Messages.LastOrDefault(m => m.Role == "tool" && m.ToolName == name && m.ToolResult == null);
        toolMsg ??= Messages.LastOrDefault(m => m.Role == "tool" && m.ToolName == name);
        if (toolMsg != null)
        {
            toolMsg.ToolResult = $"[{elapsed.TotalSeconds:F1}s] {result}";
        }
    }

    private void FinalizeResponse()
    {
        // Finalize all remaining streaming assistant messages
        foreach (var msg in Messages.Where(m => m.Role == "assistant" && m.IsStreaming))
            msg.IsStreaming = false;

        // Remove empty assistant bubbles (e.g., tool-only responses with no text)
        var empties = Messages.Where(m => m.Role == "assistant" && string.IsNullOrWhiteSpace(m.Content) && string.IsNullOrWhiteSpace(m.ThinkingContent)).ToList();
        foreach (var empty in empties)
            Messages.Remove(empty);

        IsProcessing = false;
        CanSend = true;
    }

    private void HandleError(string error)
    {
        var last = Messages.LastOrDefault(m => m.Role == "assistant" && m.IsStreaming)
                   ?? Messages.LastOrDefault(m => m.Role == "assistant");
        if (last != null)
        {
            last.Content += $"\n\n⚠️ Error: {error}";
            last.IsStreaming = false;
        }
        IsProcessing = false;
        CanSend = true;
    }

    private void UpdateContextUsage(thuvu.Models.Usage usage)
    {
        PromptTokens = usage.PromptTokens;
        CompletionTokens = usage.CompletionTokens;
        TotalTokens = usage.TotalTokens;

        // Resolve max context: API response > model config > agent config > fallback
        var max = usage.MaxContextLength ?? 0;
        if (max <= 0 && _agentService != null)
        {
            var modelId = _agentService.EffectiveModel;
            var modelEntry = thuvu.Models.ModelRegistry.Instance.GetModel(modelId);
            if (modelEntry != null && modelEntry.MaxContextLength > 0)
                max = modelEntry.MaxContextLength;
        }
        if (max <= 0 && thuvu.Models.AgentConfig.Config.MaxContextLength > 0)
            max = thuvu.Models.AgentConfig.Config.MaxContextLength;
        if (max <= 0) max = _maxContextTokens;
        if (max <= 0) max = 32768;
        MaxContextTokens = max;

        ContextUsagePercent = Math.Min(100.0, (double)usage.PromptTokens / max * 100.0);
        ContextUsageText = $"{usage.PromptTokens:N0} / {max:N0} tokens ({ContextUsagePercent:F0}%)";
    }
}
