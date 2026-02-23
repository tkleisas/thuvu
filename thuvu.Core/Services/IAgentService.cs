using thuvu.Models;

namespace thuvu.Services;

/// <summary>
/// Image data for multimodal messages (Core-level DTO without UI dependencies)
/// </summary>
public class AgentImageData
{
    public string Base64 { get; set; } = "";
    public string MimeType { get; set; } = "image/png";
}

/// <summary>
/// Abstraction over the agent execution loop.
/// Implemented by DesktopAgentService (in-process) and RemoteAgentService (HTTP+SSE).
/// </summary>
public interface IAgentService
{
    // --- Events for streaming UI updates ---
    event Action<string>? OnToken;
    event Action<string>? OnReasoningToken;
    event Action<string, string>? OnToolCall;
    event Action<string, string, string, TimeSpan>? OnToolComplete;
    event Action<string>? OnContentReplace;
    event Action? OnComplete;
    event Action<string>? OnError;
    event Action<Usage>? OnUsage;
    event Action? OnConfigReloaded;
    /// <summary>Fired at the start of each agent loop iteration. Args: current iteration, max iterations.</summary>
    event Action<int, int>? OnIteration;

    // --- State ---
    bool IsProcessing { get; }
    IReadOnlyList<ChatMessage> Messages { get; }
    string? WorkDirectory { get; set; }
    string? SessionId { get; set; }
    string? ModelOverride { get; }
    string EffectiveModel { get; }

    // --- Operations ---
    Task SendMessageAsync(string prompt);
    Task SendMessageWithImagesAsync(string prompt, IReadOnlyList<AgentImageData> images);
    void CancelRequest();
    void SetModel(string modelId);
    void SetSystemPrompt(string promptContent);
    void ClearMessages();
    void RestoreMessages(List<ChatMessage> messages);
    void ReloadConfig();
    /// <summary>
    /// Summarize the current conversation into a compact context and persist it.
    /// Returns true if summarization succeeded. Default implementation returns false.
    /// </summary>
    Task<bool> CompactAsync(Action<string>? onStatus = null, CancellationToken ct = default) => Task.FromResult(false);

    // --- Info ---
    string GetModelName();
    string GetHostUrl();
}
