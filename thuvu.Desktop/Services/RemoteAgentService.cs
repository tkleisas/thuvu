using System.Net.Http;
using System.Text;
using System.Text.Json;
using thuvu.Models;
using thuvu.Services;
using thuvu.Tools;

namespace thuvu.Desktop.Services;

/// <summary>
/// IAgentService implementation that connects to a remote thuvu agent process
/// via HTTP REST API + SSE streaming. Used for detached agent mode.
/// </summary>
public class RemoteAgentService : IAgentService, IDisposable
{
    private readonly HttpClient _http;
    private CancellationTokenSource? _currentCts;
    private string? _currentJobId;
    private readonly List<ChatMessage> _messages = new();

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public event Action<string>? OnToken;
    public event Action<string>? OnReasoningToken;
    public event Action<string, string>? OnToolCall;
    public event Action<string, string, string, TimeSpan>? OnToolComplete;
    public event Action<string>? OnContentReplace;
    public event Action? OnComplete;
    public event Action<string>? OnError;
    public event Action<Usage>? OnUsage;
    public event Action? OnConfigReloaded;
    public event Action<int, int>? OnIteration; // not fired for remote agents

    public bool IsProcessing { get; private set; }
    public IReadOnlyList<ChatMessage> Messages => _messages.AsReadOnly();
    public string? WorkDirectory { get; set; }
    public string? SessionId { get; set; }
    public string? ModelOverride { get; private set; }
    public string EffectiveModel => ModelOverride ?? _remoteModel ?? "unknown";

    /// <summary>Base URL of the remote agent (e.g. http://localhost:5001)</summary>
    public string BaseUrl { get; }

    /// <summary>Bearer token for authentication</summary>
    public string? BearerToken { get; set; }

    /// <summary>Whether the remote agent is reachable</summary>
    public bool IsConnected { get; private set; }

    private string? _remoteModel;
    private string? _remoteHostUrl;
    private Usage? _lastUsage;

    public RemoteAgentService(string baseUrl, string? bearerToken = null)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        BearerToken = bearerToken;

        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        if (!string.IsNullOrEmpty(bearerToken))
            _http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);

        _messages.Add(new ChatMessage("system", "Remote agent session"));
    }

    /// <summary>Check if the remote agent is reachable and fetch its info</summary>
    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync($"{BaseUrl}/api/agent/info", ct);
            if (!resp.IsSuccessStatusCode)
            {
                IsConnected = false;
                return false;
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            _remoteModel = doc.RootElement.TryGetProperty("model", out var m) ? m.GetString() : null;
            IsConnected = true;
            return true;
        }
        catch
        {
            IsConnected = false;
            return false;
        }
    }

    public async Task SendMessageAsync(string prompt)
    {
        if (IsProcessing) return;
        IsProcessing = true;
        _currentCts = new CancellationTokenSource();
        _messages.Add(new ChatMessage("user", prompt));
        RecordMessage("user", requestContent: prompt);

        try
        {
            await SubmitAndStreamAsync(prompt, _currentCts.Token);
        }
        catch (OperationCanceledException)
        {
            OnError?.Invoke("Request cancelled");
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Remote agent error: {ex.Message}");
        }
        finally
        {
            IsProcessing = false;
            _currentCts?.Dispose();
            _currentCts = null;
        }
    }

    public async Task SendMessageWithImagesAsync(string prompt, IReadOnlyList<AgentImageData> images)
    {
        // Remote agent doesn't support images yet — fall back to text only
        await SendMessageAsync(prompt);
    }

    private async Task SubmitAndStreamAsync(string prompt, CancellationToken ct)
    {
        // Build payload with optional model and system prompt overrides
        var jobRequest = new Dictionary<string, string?> { ["prompt"] = prompt };
        if (!string.IsNullOrEmpty(ModelOverride))
            jobRequest["model"] = ModelOverride;
        var systemMsg = _messages.FirstOrDefault(m => m.Role == "system");
        if (systemMsg != null)
            jobRequest["systemPrompt"] = systemMsg.Content;

        var payload = JsonSerializer.Serialize(jobRequest, _jsonOptions);
        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var submitResp = await _http.PostAsync($"{BaseUrl}/api/jobs", content, ct);

        if (!submitResp.IsSuccessStatusCode)
        {
            var errorBody = await submitResp.Content.ReadAsStringAsync(ct);
            OnError?.Invoke($"Failed to submit job: {errorBody}");
            return;
        }

        var submitJson = await submitResp.Content.ReadAsStringAsync(ct);
        using var submitDoc = JsonDocument.Parse(submitJson);
        var jobId = submitDoc.RootElement.GetProperty("jobId").GetString()!;
        _currentJobId = jobId;

        // Connect to SSE stream
        var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/api/jobs/{jobId}/stream");
        if (!string.IsNullOrEmpty(BearerToken))
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", BearerToken);

        var streamResp = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!streamResp.IsSuccessStatusCode)
        {
            OnError?.Invoke($"Failed to connect to event stream: {streamResp.StatusCode}");
            return;
        }

        await using var stream = await streamResp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        string? currentEventType = null;
        var dataBuilder = new StringBuilder();

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) break; // Stream closed

            if (line.StartsWith("event: "))
            {
                currentEventType = line[7..];
            }
            else if (line.StartsWith("data: "))
            {
                dataBuilder.Append(line[6..]);
            }
            else if (line.Length == 0 && currentEventType != null)
            {
                // Empty line = end of event
                var evtType = currentEventType;
                ProcessSseEvent(evtType, dataBuilder.ToString());
                currentEventType = null;
                dataBuilder.Clear();

                // If we got complete or error, we're done
                if (evtType == "complete" || evtType == "error")
                    break;
            }
        }
    }

    private void ProcessSseEvent(string eventType, string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;

            switch (eventType)
            {
                case "token":
                    if (root.TryGetProperty("text", out var tokenText))
                        OnToken?.Invoke(tokenText.GetString() ?? "");
                    break;

                case "reasoning":
                    if (root.TryGetProperty("text", out var reasonText))
                        OnReasoningToken?.Invoke(reasonText.GetString() ?? "");
                    break;

                case "tool_call":
                    var tcName = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var tcArgs = root.TryGetProperty("args", out var a) ? a.GetString() ?? "" : "";
                    OnToolCall?.Invoke(tcName, tcArgs);
                    break;

                case "tool_complete":
                    var name = root.TryGetProperty("name", out var tn) ? tn.GetString() ?? "" : "";
                    var args = root.TryGetProperty("args", out var ta) ? ta.GetString() ?? "" : "";
                    var result = root.TryGetProperty("result", out var tr) ? tr.GetString() ?? "" : "";
                    var elapsed = root.TryGetProperty("elapsed", out var te) ? te.GetDouble() : 0;
                    OnToolComplete?.Invoke(name, args, result, TimeSpan.FromSeconds(elapsed));
                    RecordMessage("tool_call", toolName: name, toolArgs: args, toolResult: result);
                    break;

                case "content_replace":
                    if (root.TryGetProperty("content", out var crContent))
                        OnContentReplace?.Invoke(crContent.GetString() ?? "");
                    break;

                case "usage":
                    var usage = JsonSerializer.Deserialize<Usage>(data, _jsonOptions);
                    if (usage != null)
                    {
                        _lastUsage = usage;
                        OnUsage?.Invoke(usage);
                    }
                    break;

                case "complete":
                    var response = root.TryGetProperty("response", out var resp) ? resp.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(response))
                    {
                        _messages.Add(new ChatMessage("assistant", response));
                        RecordMessage("assistant", responseContent: response);
                    }
                    OnComplete?.Invoke();
                    break;

                case "error":
                    var errorMsg = root.TryGetProperty("message", out var em) ? em.GetString() ?? "" : "Unknown error";
                    OnError?.Invoke(errorMsg);
                    break;
            }
        }
        catch (Exception ex)
        {
            AgentLogger.LogError("Failed to process SSE event '{Type}': {Error}", eventType, ex.Message);
        }
    }

    public void CancelRequest()
    {
        _currentCts?.Cancel();

        // Also cancel on remote side
        if (_currentJobId != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _http.DeleteAsync($"{BaseUrl}/api/jobs/{_currentJobId}");
                }
                catch { }
            });
        }
    }

    public void SetModel(string modelId)
    {
        ModelOverride = modelId;
    }

    public void SetSystemPrompt(string promptContent)
    {
        if (_messages.Count > 0 && _messages[0].Role == "system")
            _messages[0] = new ChatMessage("system", promptContent);
        else
            _messages.Insert(0, new ChatMessage("system", promptContent));
    }

    /// <summary>Fire-and-forget message recording to SQLite</summary>
    private void RecordMessage(string messageType, string? requestContent = null,
        string? responseContent = null, string? toolName = null, string? toolArgs = null,
        string? toolResult = null)
    {
        var sid = SessionId;
        if (string.IsNullOrEmpty(sid)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var record = new MessageRecord
                {
                    SessionId = sid,
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
                await SqliteService.Instance.StartMessageAsync(record);
            }
            catch { }
        });
    }

    public void ClearMessages()
    {
        _messages.Clear();
        _messages.Add(new ChatMessage("system", "Remote agent session"));
    }

    public void RestoreMessages(List<ChatMessage> messages)
    {
        if (IsProcessing) return;
        _messages.Clear();
        _messages.AddRange(messages);
    }

    public void ReloadConfig()
    {
        OnConfigReloaded?.Invoke();
    }

    public string GetModelName() => _remoteModel ?? ModelOverride ?? "remote";
    public string GetHostUrl() => BaseUrl;

    public void Dispose()
    {
        _currentCts?.Cancel();
        _currentCts?.Dispose();
        _http.Dispose();
    }
}
