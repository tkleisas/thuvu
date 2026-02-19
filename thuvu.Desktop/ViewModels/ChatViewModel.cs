using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
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
        
        Messages.Add(new ChatMessageViewModel
        {
            Role = "user",
            Content = InputText,
            Timestamp = DateTime.Now.ToString("HH:mm:ss")
        });
        
        var prompt = InputText;
        InputText = string.Empty;
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
