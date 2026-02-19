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

    /// <summary>Working directory for this agent's tools. When set, tools resolve paths relative to this.</summary>
    public string? WorkDirectory { get; set; }

    /// <summary>Override model for this agent session. Null = use global default.</summary>
    public string? ModelOverride { get; private set; }

    /// <summary>The effective model ID this agent will use</summary>
    public string EffectiveModel => ModelOverride ?? AgentConfig.Config.Model;

    /// <summary>Set a specific model for this agent session</summary>
    public void SetModel(string modelId)
    {
        if (IsProcessing) return;
        ModelOverride = modelId;

        // If model has a different host, reconfigure HttpClient
        var endpoint = ModelRegistry.Instance.GetModel(modelId);
        if (endpoint != null && !string.IsNullOrEmpty(endpoint.HostUrl))
        {
            _http.Dispose();
            _http = CreateHttpClientForEndpoint(endpoint);
        }
    }

    public DesktopAgentService()
    {
        AgentConfig.LoadConfig();
        _http = CreateHttpClient();
        _tools = BuildTools.GetToolsForSession();
        _messages = new List<ChatMessage>
        {
            new("system", SystemPromptManager.Instance.GetCurrentSystemPrompt())
        };

        // Wire up permission prompts for Desktop (Console.ReadKey not available in GUI apps)
        if (PermissionManager.AsyncPermissionPrompt == null)
        {
            if (AgentConfig.Config.AutoApproveTuiTools)
            {
                PermissionManager.AsyncPermissionPrompt = (toolName, argsJson) =>
                    Task.FromResult('S'); // Auto-approve for session
            }
            else
            {
                PermissionManager.AsyncPermissionPrompt = async (toolName, argsJson) =>
                {
                    var tcs = new TaskCompletionSource<char>();
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        var dialog = new thuvu.Desktop.Views.PermissionDialog(toolName, argsJson);
                        // Find the top-level window to use as owner
                        var topLevel = Avalonia.Application.Current?.ApplicationLifetime is
                            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                            ? desktop.MainWindow : null;
                        if (topLevel != null)
                            await dialog.ShowDialog(topLevel);
                        else
                            dialog.Show();

                        var result = dialog.Result switch
                        {
                            "always" => 'A',
                            "session" => 'S',
                            "once" => 'O',
                            _ => 'N'
                        };
                        tcs.SetResult(result);
                    });
                    return await tcs.Task;
                };
            }
        }
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

    private static HttpClient CreateHttpClientForEndpoint(ModelEndpoint endpoint)
    {
        var client = new HttpClient();
        client.BaseAddress = new Uri(endpoint.HostUrl.TrimEnd('/') + "/");
        client.Timeout = TimeSpan.FromMinutes(endpoint.TimeoutMinutes > 0 ? endpoint.TimeoutMinutes : 60);
        if (!string.IsNullOrEmpty(endpoint.AuthToken))
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue(
                    string.IsNullOrEmpty(endpoint.AuthScheme) ? "Bearer" : endpoint.AuthScheme,
                    endpoint.AuthToken);
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
            if (!string.IsNullOrEmpty(WorkDirectory))
            {
                var ctx = AgentContext.CreateContext("desktop", WorkDirectory);
                await AgentContext.RunInContextAsync(ctx, () => ExecuteAgentLoopAsync());
            }
            else
            {
                await ExecuteAgentLoopAsync();
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

    private async Task ExecuteAgentLoopAsync()
    {
        var model = EffectiveModel;
        if (AgentConfig.Config.Stream)
        {
            await AgentLoop.CompleteWithToolsStreamingAsync(
                _http,
                model,
                _messages,
                _tools,
                _currentCts!.Token,
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
            await AgentLoop.CompleteWithToolsAsync(
                _http,
                model,
                _messages,
                _tools,
                _currentCts!.Token,
                onToolResult: (name, json) => OnToolCall?.Invoke(name, json),
                onToolComplete: (name, args, res, elapsed) => OnToolComplete?.Invoke(name, args, res, elapsed),
                onToolCall: (name, args) => OnToolCall?.Invoke(name, args)
            );
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
