using System.Text.Json;
using System.Text.Json.Serialization;
using thuvu.Models;

namespace thuvu.Desktop.Services;

/// <summary>
/// Persists agent session state (conversation history + UI messages) to disk.
/// Stores files in .thuvu-sessions/ alongside the .thuvu project file.
/// </summary>
public class SessionStore
{
    private readonly string _sessionsDir;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public SessionStore(string projectDirectory)
    {
        _sessionsDir = Path.Combine(projectDirectory, ".thuvu-sessions");
    }

    /// <summary>Save a single session's state to disk</summary>
    public void SaveSession(SessionData session)
    {
        Directory.CreateDirectory(_sessionsDir);
        var path = Path.Combine(_sessionsDir, $"{session.Id}.json");
        var json = JsonSerializer.Serialize(session, _jsonOptions);
        File.WriteAllText(path, json);
    }

    /// <summary>Load a single session from disk</summary>
    public SessionData? LoadSession(string sessionId)
    {
        var path = Path.Combine(_sessionsDir, $"{sessionId}.json");
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<SessionData>(json, _jsonOptions);
        }
        catch { return null; }
    }

    /// <summary>Delete a session file</summary>
    public void DeleteSession(string sessionId)
    {
        var path = Path.Combine(_sessionsDir, $"{sessionId}.json");
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>Save the session index (list of open sessions + active tab)</summary>
    public void SaveIndex(SessionIndex index)
    {
        Directory.CreateDirectory(_sessionsDir);
        var path = Path.Combine(_sessionsDir, "session-index.json");
        var json = JsonSerializer.Serialize(index, _jsonOptions);
        File.WriteAllText(path, json);
    }

    /// <summary>Load the session index</summary>
    public SessionIndex? LoadIndex()
    {
        var path = Path.Combine(_sessionsDir, "session-index.json");
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<SessionIndex>(json, _jsonOptions);
        }
        catch { return null; }
    }

    /// <summary>Save all active sessions and the index atomically</summary>
    public void SaveAll(SessionIndex index, IEnumerable<SessionData> sessions)
    {
        Directory.CreateDirectory(_sessionsDir);
        foreach (var session in sessions)
            SaveSession(session);
        SaveIndex(index);
    }
}

/// <summary>Tracks which sessions exist and which tab was active</summary>
public class SessionIndex
{
    public string? ActiveSessionId { get; set; }
    public List<SessionSummary> Sessions { get; set; } = new();
}

public class SessionSummary
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? ModelId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public int MessageCount { get; set; }
}

/// <summary>Full state for a single chat session</summary>
public class SessionData
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? ModelId { get; set; }

    /// <summary>Core ChatMessage list used by AgentLoop for continued conversation</summary>
    public List<ChatMessage> Messages { get; set; } = new();

    /// <summary>UI message data for restoring the chat display exactly as the user saw it</summary>
    public List<UiMessage> UiMessages { get; set; } = new();
}

/// <summary>Serializable snapshot of a ChatMessageViewModel</summary>
public class UiMessage
{
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
    public string Timestamp { get; set; } = "";
    public string? ToolName { get; set; }
    public string? ToolArgs { get; set; }
    public string? ToolResult { get; set; }
    public string? ThinkingContent { get; set; }
}
