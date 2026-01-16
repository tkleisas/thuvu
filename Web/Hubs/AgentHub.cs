using Microsoft.AspNetCore.SignalR;
using thuvu.Web.Services;

namespace thuvu.Web.Hubs
{
    /// <summary>
    /// SignalR hub for real-time agent communication
    /// </summary>
    public class AgentHub : Hub
    {
        private readonly WebAgentService _agentService;

        public AgentHub(WebAgentService agentService)
        {
            _agentService = agentService;
        }

        /// <summary>
        /// Create or join a session
        /// </summary>
        public async Task<object> JoinSession(string? sessionId = null)
        {
            var session = _agentService.GetOrCreateSession(sessionId);
            await Groups.AddToGroupAsync(Context.ConnectionId, session.SessionId);
            
            return new
            {
                sessionId = session.SessionId,
                messageCount = session.Messages.Count,
                createdAt = session.CreatedAt,
                config = _agentService.GetConfig()
            };
        }

        /// <summary>
        /// Send a message and stream the response
        /// </summary>
        public async IAsyncEnumerable<AgentStreamEvent> SendMessage(
            string sessionId, 
            string message,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var evt in _agentService.SendMessageAsync(sessionId, message, ct))
            {
                yield return evt;
            }
        }

        /// <summary>
        /// Send a message with an image attachment and stream the response
        /// </summary>
        public async IAsyncEnumerable<AgentStreamEvent> SendMessageWithImage(
            string sessionId, 
            string message,
            string imageBase64,
            string imageMimeType,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var evt in _agentService.SendMessageWithImageAsync(sessionId, message, imageBase64, imageMimeType, ct))
            {
                yield return evt;
            }
        }

        /// <summary>
        /// Cancel the current request
        /// </summary>
        public bool CancelRequest(string sessionId)
        {
            return _agentService.CancelRequest(sessionId);
        }

        /// <summary>
        /// Clear session history
        /// </summary>
        public void ClearSession(string sessionId)
        {
            _agentService.ClearSession(sessionId);
        }
        
        /// <summary>
        /// Keep-alive ping to prevent connection timeout in background tabs
        /// </summary>
        public DateTime Ping()
        {
            return DateTime.UtcNow;
        }
        
        /// <summary>
        /// Respond to a permission request
        /// </summary>
        /// <param name="sessionId">Session ID</param>
        /// <param name="requestId">Permission request ID</param>
        /// <param name="choice">A=Always, S=Session, O=Once, N=No</param>
        public bool RespondToPermission(string sessionId, string requestId, string choice)
        {
            if (string.IsNullOrEmpty(choice) || choice.Length < 1)
                return false;
                
            return _agentService.RespondToPermission(sessionId, requestId, char.ToUpper(choice[0]));
        }

        /// <summary>
        /// Get session history
        /// </summary>
        public object? GetHistory(string sessionId)
        {
            var session = _agentService.GetSession(sessionId);
            if (session == null) return null;

            return session.Messages
                .Where(m => m.Role != "system" && m.Role != "tool")
                .Select(m => new
                {
                    role = m.Role,
                    content = m.Content,
                    timestamp = DateTime.Now // Would need to store this per message
                })
                .ToList();
        }

        /// <summary>
        /// Get current config
        /// </summary>
        public object GetConfig()
        {
            return _agentService.GetConfig();
        }

        /// <summary>
        /// Get list of recent sessions that can be restored
        /// </summary>
        public async Task<List<SessionSummary>> GetRecentSessions(int limit = 10)
        {
            return await _agentService.GetRecentSessionsAsync(limit);
        }

        /// <summary>
        /// Get file suggestions for autocomplete
        /// </summary>
        public List<string> GetFileSuggestions(string sessionId, string prefix)
        {
            return _agentService.GetFileSuggestions(prefix);
        }

        /// <summary>
        /// Execute a slash command
        /// </summary>
        public async IAsyncEnumerable<AgentStreamEvent> ExecuteCommand(
            string sessionId,
            string command,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var evt in _agentService.ExecuteCommandAsync(sessionId, command, ct))
            {
                yield return evt;
            }
        }

        /// <summary>
        /// Get directory contents for file tree
        /// </summary>
        public List<FileTreeItem> GetDirectoryContents(string sessionId, string path)
        {
            return _agentService.GetDirectoryContents(path);
        }

        /// <summary>
        /// Read file contents
        /// </summary>
        public FileContentResult ReadFile(string sessionId, string path)
        {
            return _agentService.ReadFile(path);
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            // Could clean up sessions here if needed
            await base.OnDisconnectedAsync(exception);
        }
    }

    public class FileTreeItem
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public bool IsDirectory { get; set; }
        public bool IsExpanded { get; set; }
        public bool IsLoaded { get; set; }
        public List<FileTreeItem> Children { get; set; } = new();
    }

    public class FileContentResult
    {
        public bool Success { get; set; }
        public string Content { get; set; } = "";
        public string? Error { get; set; }
        public bool IsBinary { get; set; }
    }
}
