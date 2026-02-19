using System.Collections.Concurrent;
using System.Text.Json;
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

    /// <summary>Session ID for SQLite message recording (set by AgentRegistry)</summary>
    public string? SessionId { get; set; }

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
            _http = CreateHttpClientWithLoggingForEndpoint(endpoint);
        }
    }

    public DesktopAgentService()
    {
        AgentConfig.LoadConfig();
        _http = CreateHttpClientWithLogging();
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
        _http = CreateHttpClientWithLogging();
        OnConfigReloaded?.Invoke();
    }

    private string? GetStreamLogDir()
    {
        if (string.IsNullOrEmpty(WorkDirectory)) return null;
        return Path.Combine(WorkDirectory, ".stream");
    }

    private HttpClient CreateHttpClientWithLogging()
    {
        var inner = new HttpClientHandler();
        var handler = new StreamLogHandler(GetStreamLogDir, inner);
        var client = new HttpClient(handler);
        AgentConfig.ApplyConfig(client);
        return client;
    }

    private HttpClient CreateHttpClientWithLoggingForEndpoint(ModelEndpoint endpoint)
    {
        var inner = new HttpClientHandler();
        var handler = new StreamLogHandler(GetStreamLogDir, inner);
        var client = new HttpClient(handler);
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

        // Record user message to SQLite
        RecordMessageAsync(SessionId, "user", requestContent: prompt);

        try
        {
            string? response;
            if (!string.IsNullOrEmpty(WorkDirectory))
            {
                var ctx = AgentContext.CreateContext("desktop", WorkDirectory);
                response = await AgentContext.RunInContextAsync(ctx, () => ExecuteAgentLoopAsync());
            }
            else
            {
                response = await ExecuteAgentLoopAsync();
            }

            // Record assistant response using the returned content
            // (AgentLoop returns the final text but doesn't add it to _messages)
            if (!string.IsNullOrEmpty(response))
                RecordMessageAsync(SessionId, "assistant", responseContent: response);

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

    private async Task<string?> ExecuteAgentLoopAsync()
    {
        var model = EffectiveModel;
        var sid = SessionId;
        string? response;
        if (AgentConfig.Config.Stream)
        {
            response = await AgentLoop.CompleteWithToolsStreamingAsync(
                _http,
                model,
                _messages,
                _tools,
                _currentCts!.Token,
                onToolResult: (name, json) => OnToolCall?.Invoke(name, json),
                onToolComplete: (name, args, res, elapsed) =>
                {
                    OnToolComplete?.Invoke(name, args, res, elapsed);
                    RecordMessageAsync(sid, "tool_call", toolName: name, toolArgs: args,
                        toolResult: res, durationMs: (long)elapsed.TotalMilliseconds);
                },
                onToolCall: (name, args) => OnToolCall?.Invoke(name, args),
                onToken: token => OnToken?.Invoke(token),
                onUsage: usage =>
                {
                    OnUsage?.Invoke(usage);
                    _lastUsage = usage;
                },
                onReasoningToken: token => OnReasoningToken?.Invoke(token)
            );
        }
        else
        {
            response = await AgentLoop.CompleteWithToolsAsync(
                _http,
                model,
                _messages,
                _tools,
                _currentCts!.Token,
                onToolResult: (name, json) => OnToolCall?.Invoke(name, json),
                onToolComplete: (name, args, res, elapsed) =>
                {
                    OnToolComplete?.Invoke(name, args, res, elapsed);
                    RecordMessageAsync(sid, "tool_call", toolName: name, toolArgs: args,
                        toolResult: res, durationMs: (long)elapsed.TotalMilliseconds);
                },
                onToolCall: (name, args) => OnToolCall?.Invoke(name, args)
            );
        }
        return response;
    }

    private Usage? _lastUsage;

    /// <summary>Fire-and-forget message recording to SQLite</summary>
    private void RecordMessageAsync(string? sessionId, string messageType,
        string? requestContent = null, string? responseContent = null,
        string? toolName = null, string? toolArgs = null, string? toolResult = null,
        long durationMs = 0)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var record = new MessageRecord
                {
                    SessionId = sessionId,
                    AgentId = "desktop",
                    StartedAt = DateTime.Now,
                    AgentRole = "main",
                    AgentDepth = 0,
                    ModelId = EffectiveModel,
                    MessageType = messageType,
                    RequestContent = requestContent,
                    ResponseContent = responseContent,
                    ToolName = toolName,
                    ToolArgsJson = toolArgs,
                    ToolResultJson = toolResult,
                    PromptTokens = _lastUsage?.PromptTokens,
                    CompletionTokens = _lastUsage?.CompletionTokens,
                    TotalTokens = _lastUsage?.TotalTokens,
                    Status = "completed"
                };
                var id = await SqliteService.Instance.StartMessageAsync(record);
                if (durationMs > 0)
                {
                    await SqliteService.Instance.CompleteMessageAsync(id, new MessageCompleteInfo
                    {
                        CompletedAt = DateTime.Now,
                        DurationMs = durationMs,
                        ToolName = toolName,
                        ToolArgsJson = toolArgs,
                        ToolResultJson = toolResult
                    });
                }
            }
            catch { }
        });
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
        // Clear messages in DB but keep the session
        if (!string.IsNullOrEmpty(SessionId))
        {
            try { SqliteService.Instance.DeleteSessionMessagesAsync(SessionId).GetAwaiter().GetResult(); }
            catch { }
        }
    }

    /// <summary>Replace the message history with a previously saved one (for session restore)</summary>
    public void RestoreMessages(List<ChatMessage> messages)
    {
        if (IsProcessing) return;
        _messages = messages;
    }

    public string GetModelName() => AgentConfig.Config.Model;
    public string GetHostUrl() => AgentConfig.Config.HostUrl;

    /// <summary>
    /// DelegatingHandler that logs full HTTP request/response bodies to .stream/ directory.
    /// </summary>
    private class StreamLogHandler : DelegatingHandler
    {
        private readonly Func<string?> _getLogDir;
        private int _requestCounter;

        public StreamLogHandler(Func<string?> getLogDir, HttpMessageHandler inner) : base(inner)
        {
            _getLogDir = getLogDir;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var logDir = _getLogDir();
            if (string.IsNullOrEmpty(logDir))
                return await base.SendAsync(request, cancellationToken);

            Directory.CreateDirectory(logDir);
            var seq = Interlocked.Increment(ref _requestCounter);
            var timestamp = DateTime.Now.ToString("HHmmss_fff");
            var prefix = Path.Combine(logDir, $"{timestamp}_{seq:D4}");

            // Log request
            string? requestBody = null;
            if (request.Content != null)
            {
                requestBody = await request.Content.ReadAsStringAsync(cancellationToken);
                await File.WriteAllTextAsync($"{prefix}_request.json", FormatJson(requestBody), cancellationToken);
                // Re-create content since it was consumed
                request.Content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json");
            }

            HttpResponseMessage response;
            try
            {
                response = await base.SendAsync(request, cancellationToken);
            }
            catch (Exception ex)
            {
                await File.WriteAllTextAsync($"{prefix}_error.txt",
                    $"Exception: {ex.GetType().Name}\n{ex.Message}\n\n{ex.StackTrace}", cancellationToken);
                throw;
            }

            // Log response
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var logContent = $"HTTP {(int)response.StatusCode} {response.StatusCode}\n\n{FormatJson(responseBody)}";
            await File.WriteAllTextAsync($"{prefix}_response.json", logContent, cancellationToken);

            // Response body was consumed, create a new response with the same content
            var newContent = new StringContent(responseBody, System.Text.Encoding.UTF8, response.Content.Headers.ContentType?.MediaType ?? "application/json");
            response.Content = newContent;

            return response;
        }

        private static string FormatJson(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
            }
            catch
            {
                return json;
            }
        }
    }
}
