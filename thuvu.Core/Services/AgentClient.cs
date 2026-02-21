using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using thuvu.Models;

namespace thuvu.Services;

/// <summary>
/// Shared HTTP+SSE client for the conversation-based agent API.
/// Implements IAgentService so any UI can use it as a drop-in replacement
/// for direct in-process agent execution.
///
/// Usage:
///   var client = new AgentClient("http://localhost:5001", "token123");
///   await client.ConnectAsync();
///   await client.CreateConversationAsync();
///   await client.SendMessageAsync("Hello");
/// </summary>
public class AgentClient : IAgentService, IDisposable
{
    private readonly HttpClient _http;
    private readonly List<ChatMessage> _messages = new();
    private CancellationTokenSource? _currentCts;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    // IAgentService events
    public event Action<string>? OnToken;
    public event Action<string>? OnReasoningToken;
    public event Action<string, string>? OnToolCall;
    public event Action<string, string, string, TimeSpan>? OnToolComplete;
    public event Action<string>? OnContentReplace;
    public event Action? OnComplete;
    public event Action<string>? OnError;
    public event Action<Usage>? OnUsage;
    public event Action? OnConfigReloaded;

    // Permission prompt events
    public event Func<string, string, string, string, Task<bool>>? OnPermissionRequest;

    // IAgentService state
    public bool IsProcessing { get; private set; }
    public IReadOnlyList<ChatMessage> Messages => _messages.AsReadOnly();
    public string? WorkDirectory { get; set; }
    public string? SessionId { get; set; }
    public string? ModelOverride { get; private set; }
    public string EffectiveModel => ModelOverride ?? _remoteModel ?? "unknown";

    // Connection state
    public string BaseUrl { get; }
    public string? BearerToken { get; set; }
    public bool IsConnected { get; private set; }
    public string? ConversationId { get; private set; }

    // Server info
    private string? _remoteModel;
    private string? _agentName;
    private Usage? _lastUsage;

    public AgentClient(string baseUrl, string? bearerToken = null)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        BearerToken = bearerToken;

        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        if (!string.IsNullOrEmpty(bearerToken))
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", bearerToken);
    }

    /// <summary>
    /// Check if the server is reachable and fetch its info.
    /// </summary>
    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync($"{BaseUrl}/api/health", ct);
            if (!resp.IsSuccessStatusCode)
            {
                IsConnected = false;
                return false;
            }

            // Also fetch config for model info
            var configResp = await _http.GetAsync($"{BaseUrl}/api/config", ct);
            if (configResp.IsSuccessStatusCode)
            {
                var json = await configResp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("model", out var model) &&
                    model.TryGetProperty("current", out var current))
                    _remoteModel = current.GetString();
                if (doc.RootElement.TryGetProperty("agent", out var agent) &&
                    agent.TryGetProperty("name", out var name))
                    _agentName = name.GetString();
            }

            IsConnected = true;
            return true;
        }
        catch
        {
            IsConnected = false;
            return false;
        }
    }

    /// <summary>
    /// Create a new conversation on the server. Must be called before SendMessageAsync.
    /// </summary>
    public async Task<string?> CreateConversationAsync(string? model = null, string? systemPrompt = null, CancellationToken ct = default)
    {
        var body = new Dictionary<string, string?>();
        if (!string.IsNullOrEmpty(model)) body["model"] = model;
        if (!string.IsNullOrEmpty(systemPrompt)) body["systemPrompt"] = systemPrompt;
        if (!string.IsNullOrEmpty(WorkDirectory)) body["workDirectory"] = WorkDirectory;

        var content = new StringContent(JsonSerializer.Serialize(body, _jsonOptions), Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync($"{BaseUrl}/api/conversations", content, ct);

        if (!resp.IsSuccessStatusCode) return null;

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        ConversationId = doc.RootElement.GetProperty("id").GetString();

        _messages.Clear();
        if (!string.IsNullOrEmpty(systemPrompt))
            _messages.Add(new ChatMessage("system", systemPrompt));

        return ConversationId;
    }

    /// <summary>
    /// IAgentService.SendMessageAsync — sends a message and streams the response via events.
    /// Auto-creates a conversation if none exists.
    /// </summary>
    public async Task SendMessageAsync(string prompt)
    {
        if (IsProcessing) return;

        if (ConversationId == null)
        {
            var id = await CreateConversationAsync(ModelOverride);
            if (id == null)
            {
                OnError?.Invoke("Failed to create conversation");
                return;
            }
        }

        IsProcessing = true;
        _currentCts = new CancellationTokenSource();
        _messages.Add(new ChatMessage("user", prompt));

        try
        {
            await SendAndStreamAsync(prompt, _currentCts.Token);
        }
        catch (OperationCanceledException)
        {
            OnError?.Invoke("Request cancelled");
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Agent error: {ex.Message}");
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
        // TODO: Send images as base64 in the request body
        await SendMessageAsync(prompt);
    }

    private async Task SendAndStreamAsync(string prompt, CancellationToken ct)
    {
        var body = new Dictionary<string, object?> { ["content"] = prompt };
        var payload = JsonSerializer.Serialize(body, _jsonOptions);
        var content = new StringContent(payload, Encoding.UTF8, "application/json");

        // Use SendAsync with ResponseHeadersRead for streaming
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"{BaseUrl}/api/conversations/{ConversationId}/messages")
        {
            Content = content
        };

        var resp = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!resp.IsSuccessStatusCode)
        {
            var errorBody = await resp.Content.ReadAsStringAsync(ct);
            OnError?.Invoke($"Failed to send message: {errorBody}");
            return;
        }

        // Parse SSE stream
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        await ParseSseStreamAsync(reader, ct);
    }

    /// <summary>
    /// Parse SSE events from a stream and dispatch to IAgentService events.
    /// Shared logic that can be reused by any SSE-based communication.
    /// </summary>
    private async Task ParseSseStreamAsync(StreamReader reader, CancellationToken ct)
    {
        string? currentEventType = null;
        var dataBuilder = new StringBuilder();

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) break;

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
                var evtType = currentEventType;
                await ProcessSseEventAsync(evtType, dataBuilder.ToString());
                currentEventType = null;
                dataBuilder.Clear();

                if (evtType == "complete" || evtType == "error")
                    break;
            }
        }
    }

    private async Task ProcessSseEventAsync(string eventType, string data)
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
                    var response = root.TryGetProperty("response", out var resp)
                        ? resp.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(response))
                        _messages.Add(new ChatMessage("assistant", response));
                    OnComplete?.Invoke();
                    break;

                case "error":
                    var errorMsg = root.TryGetProperty("message", out var em)
                        ? em.GetString() ?? "" : "Unknown error";
                    OnError?.Invoke(errorMsg);
                    break;

                case "permission_request":
                    if (OnPermissionRequest != null)
                    {
                        var permId = root.GetProperty("id").GetString() ?? "";
                        var tool = root.TryGetProperty("tool", out var pt) ? pt.GetString() ?? "" : "";
                        var permArgs = root.TryGetProperty("args", out var pa) ? pa.GetString() ?? "" : "";
                        var desc = root.TryGetProperty("description", out var pd) ? pd.GetString() ?? "" : "";

                        var approved = await OnPermissionRequest(permId, tool, permArgs, desc);
                        await RespondToPermissionAsync(permId, approved);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            AgentLogger.LogError("Failed to process SSE event '{Type}': {Error}", eventType, ex.Message);
        }
    }

    /// <summary>
    /// Execute a slash command on the server.
    /// </summary>
    public async Task<(bool Success, string Output, string? Error)> SendCommandAsync(
        string command, CancellationToken ct = default)
    {
        if (ConversationId == null)
            return (false, "", "No active conversation");

        var body = JsonSerializer.Serialize(new { command }, _jsonOptions);
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync(
            $"{BaseUrl}/api/conversations/{ConversationId}/command", content, ct);

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var success = root.TryGetProperty("success", out var s) && s.GetBoolean();
        var output = root.TryGetProperty("output", out var o) ? o.GetString() ?? "" : "";
        var error = root.TryGetProperty("error", out var e) ? e.GetString() : null;

        return (success, output, error);
    }

    /// <summary>
    /// Get conversation message history from the server.
    /// </summary>
    public async Task<List<ChatMessage>> GetMessagesAsync(CancellationToken ct = default)
    {
        if (ConversationId == null) return new List<ChatMessage>();

        var resp = await _http.GetAsync(
            $"{BaseUrl}/api/conversations/{ConversationId}/messages", ct);
        if (!resp.IsSuccessStatusCode) return new List<ChatMessage>();

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        var result = new List<ChatMessage>();
        if (doc.RootElement.TryGetProperty("messages", out var msgs))
        {
            foreach (var msg in msgs.EnumerateArray())
            {
                var role = msg.GetProperty("role").GetString() ?? "";
                var content = msg.GetProperty("content").GetString() ?? "";
                result.Add(new ChatMessage(role, content));
            }
        }
        return result;
    }

    /// <summary>
    /// List all conversations on the server.
    /// </summary>
    public async Task<List<ConversationInfo>> ListConversationsAsync(CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"{BaseUrl}/api/conversations", ct);
        if (!resp.IsSuccessStatusCode) return new List<ConversationInfo>();

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        var result = new List<ConversationInfo>();
        if (doc.RootElement.TryGetProperty("conversations", out var convs))
        {
            foreach (var c in convs.EnumerateArray())
            {
                result.Add(new ConversationInfo
                {
                    Id = c.GetProperty("id").GetString() ?? "",
                    Status = c.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "",
                    MessageCount = c.TryGetProperty("messageCount", out var mc) ? mc.GetInt32() : 0,
                    Model = c.TryGetProperty("model", out var m) ? m.GetString() : null,
                    LastActivityAt = c.TryGetProperty("lastActivityAt", out var la) 
                        ? la.GetDateTime() : DateTime.MinValue
                });
            }
        }
        return result;
    }

    /// <summary>Respond to a permission prompt from the server.</summary>
    private async Task RespondToPermissionAsync(string permissionId, bool approved, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new { approved }, _jsonOptions);
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        await _http.PostAsync($"{BaseUrl}/api/permissions/{permissionId}", content, ct);
    }

    public void CancelRequest()
    {
        _currentCts?.Cancel();

        if (ConversationId != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _http.PostAsync($"{BaseUrl}/api/conversations/{ConversationId}/cancel",
                        new StringContent("", Encoding.UTF8), CancellationToken.None);
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

    public void ClearMessages()
    {
        _messages.Clear();
        ConversationId = null; // Will create new conversation on next message
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

    public string GetModelName() => _remoteModel ?? ModelOverride ?? "unknown";
    public string GetHostUrl() => BaseUrl;

    public void Dispose()
    {
        _currentCts?.Cancel();
        _currentCts?.Dispose();
        _http.Dispose();
    }
}

/// <summary>
/// Lightweight info about a conversation on the server.
/// </summary>
public class ConversationInfo
{
    public string Id { get; set; } = "";
    public string Status { get; set; } = "";
    public int MessageCount { get; set; }
    public string? Model { get; set; }
    public DateTime LastActivityAt { get; set; }
}
