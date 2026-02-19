using System.Collections.Concurrent;
using thuvu.Models;
using thuvu.Tools;

namespace thuvu.Desktop.Services;

/// <summary>
/// Service bridging the AgentLoop to the desktop UI.
/// Manages sessions, streaming, tool execution and permission requests.
/// </summary>
public class DesktopAgentService
{
    private HttpClient _http;
    private readonly List<Tool> _tools;
    private List<ChatMessage> _messages;
    private CancellationTokenSource? _currentCts;

    public event Action<string>? OnToken;
    public event Action<string>? OnReasoningToken;
    public event Action<string, string>? OnToolCall;
    public event Action<string, string, string, TimeSpan>? OnToolComplete;
    public event Action<Usage>? OnUsage;
    public event Action<string>? OnError;
    public event Action? OnComplete;
    public event Action? OnConfigReloaded;

    public bool IsProcessing { get; private set; }
    public IReadOnlyList<ChatMessage> Messages => _messages.AsReadOnly();

    public DesktopAgentService()
    {
        AgentConfig.LoadConfig();
        _http = CreateHttpClient();
        _tools = BuildTools.GetToolsForSession();
        _messages = new List<ChatMessage>
        {
            new("system", SystemPromptManager.Instance.GetCurrentSystemPrompt())
        };
    }

    /// <summary>
    /// Reload configuration and recreate the HttpClient.
    /// Call after settings are saved.
    /// </summary>
    public void ReloadConfig()
    {
        if (IsProcessing) return;
        _http.Dispose();
        _http = CreateHttpClient();
        OnConfigReloaded?.Invoke();
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        AgentConfig.ApplyConfig(client);
        return client;
    }

    public async Task SendMessageAsync(string prompt)
    {
        if (IsProcessing) return;
        IsProcessing = true;
        _currentCts = new CancellationTokenSource();

        _messages.Add(new ChatMessage("user", prompt));

        try
        {
            string? result;
            if (AgentConfig.Config.Stream)
            {
                result = await AgentLoop.CompleteWithToolsStreamingAsync(
                    _http,
                    AgentConfig.Config.Model,
                    _messages,
                    _tools,
                    _currentCts.Token,
                    onToolResult: (name, json) => OnToolCall?.Invoke(name, json),
                    onToolComplete: (name, args, res, elapsed) => OnToolComplete?.Invoke(name, args, res, elapsed),
                    onToolCall: (name, args) => OnToolCall?.Invoke(name, args),
                    onToken: token => OnToken?.Invoke(token),
                    onUsage: usage => OnUsage?.Invoke(usage),
                    onReasoningToken: token => OnReasoningToken?.Invoke(token)
                );
            }
            else
            {
                result = await AgentLoop.CompleteWithToolsAsync(
                    _http,
                    AgentConfig.Config.Model,
                    _messages,
                    _tools,
                    _currentCts.Token,
                    onToolResult: (name, json) => OnToolCall?.Invoke(name, json),
                    onToolComplete: (name, args, res, elapsed) => OnToolComplete?.Invoke(name, args, res, elapsed),
                    onToolCall: (name, args) => OnToolCall?.Invoke(name, args)
                );
            }

            OnComplete?.Invoke();
        }
        catch (OperationCanceledException)
        {
            OnError?.Invoke("Request cancelled");
        }
        catch (Exception ex)
        {
            OnError?.Invoke(ex.Message);
        }
        finally
        {
            IsProcessing = false;
            _currentCts?.Dispose();
            _currentCts = null;
        }
    }

    public void CancelRequest()
    {
        _currentCts?.Cancel();
    }

    public void ClearMessages()
    {
        _messages = new List<ChatMessage>
        {
            new("system", SystemPromptManager.Instance.GetCurrentSystemPrompt())
        };
    }

    public string GetModelName() => AgentConfig.Config.Model;
    public string GetHostUrl() => AgentConfig.Config.HostUrl;
}
