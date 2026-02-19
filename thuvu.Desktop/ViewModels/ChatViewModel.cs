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

        // Unknown slash command - don't send to LLM
        if (!trimmed.Contains(' ') || trimmed.Split(' ')[0].Length < 20)
        {
            AddSystemMessage($"Unknown command: {trimmed.Split(' ')[0]}. Type /help for available commands.");
            return true;
        }

        return false;
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
