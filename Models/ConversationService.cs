using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace thuvu.Models
{
    /// <summary>
    /// Manages multiple concurrent conversations for the client/server architecture.
    /// Each conversation has its own message history, streaming channel, and cancellation.
    /// </summary>
    public class ConversationService
    {
        private static ConversationService? _instance;
        public static ConversationService Instance => _instance ?? throw new InvalidOperationException("ConversationService not initialized");

        private readonly ConcurrentDictionary<string, Conversation> _conversations = new();
        private readonly int _maxConversations;

        public ConversationService(int maxConversations = 20)
        {
            _maxConversations = maxConversations;
            _instance = this;
        }

        public static void Initialize(int maxConversations = 20)
        {
            _instance = new ConversationService(maxConversations);
        }

        public Conversation CreateConversation(string? model = null, string? systemPrompt = null, string? workDirectory = null)
        {
            if (_conversations.Count >= _maxConversations)
            {
                // Remove oldest completed conversation
                var oldest = _conversations.Values
                    .Where(c => c.Status == ConversationStatus.Idle || c.Status == ConversationStatus.Closed)
                    .OrderBy(c => c.LastActivityAt)
                    .FirstOrDefault();

                if (oldest != null)
                    _conversations.TryRemove(oldest.Id, out _);
                else
                    throw new InvalidOperationException($"Maximum conversations ({_maxConversations}) reached");
            }

            var conversation = new Conversation
            {
                Model = model,
                SystemPrompt = systemPrompt,
                WorkDirectory = workDirectory
            };

            _conversations[conversation.Id] = conversation;
            return conversation;
        }

        public Conversation? GetConversation(string id)
        {
            _conversations.TryGetValue(id, out var conv);
            return conv;
        }

        public List<Conversation> GetAllConversations()
        {
            return _conversations.Values.OrderByDescending(c => c.LastActivityAt).ToList();
        }

        public bool RemoveConversation(string id)
        {
            if (_conversations.TryRemove(id, out var conv))
            {
                conv.Dispose();
                return true;
            }
            return false;
        }
    }

    public enum ConversationStatus
    {
        Idle,
        Processing,
        AwaitingPermission,
        Closed
    }

    public class Conversation : IDisposable
    {
        public string Id { get; } = Guid.NewGuid().ToString("N")[..12];
        public ConversationStatus Status { get; set; } = ConversationStatus.Idle;
        public DateTime CreatedAt { get; } = DateTime.UtcNow;
        public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;

        // Configuration overrides (null = use server defaults)
        public string? Model { get; set; }
        public string? SystemPrompt { get; set; }
        public string? WorkDirectory { get; set; }

        // Message history (LLM format)
        public List<ConversationMessage> Messages { get; } = new();

        // SSE event streaming
        private Channel<AgentStreamEvent>? _eventChannel;
        private CancellationTokenSource? _processingCts;

        // Permission prompt relay
        private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingPermissions = new();

        public CancellationToken GetProcessingToken()
        {
            _processingCts?.Dispose();
            _processingCts = new CancellationTokenSource();
            return _processingCts.Token;
        }

        public void CancelProcessing()
        {
            _processingCts?.Cancel();
        }

        public ChannelReader<AgentStreamEvent>? CreateEventChannel()
        {
            _eventChannel = Channel.CreateUnbounded<AgentStreamEvent>(new UnboundedChannelOptions
            {
                SingleWriter = false,
                SingleReader = true
            });
            return _eventChannel.Reader;
        }

        public void EmitEvent(AgentStreamEvent evt)
        {
            _eventChannel?.Writer.TryWrite(evt);
        }

        public void CompleteEventChannel()
        {
            _eventChannel?.Writer.TryComplete();
            _eventChannel = null;
        }

        public ChannelReader<AgentStreamEvent>? GetEventReader()
        {
            return _eventChannel?.Reader;
        }

        // Permission prompt relay
        public string RequestPermission(string tool, string args, string description)
        {
            var permId = Guid.NewGuid().ToString("N")[..8];
            var tcs = new TaskCompletionSource<bool>();
            _pendingPermissions[permId] = tcs;

            EmitEvent(new AgentStreamEvent
            {
                Type = "permission_request",
                Data = JsonSerializer.Serialize(new
                {
                    id = permId,
                    tool,
                    args,
                    description
                })
            });

            return permId;
        }

        public async Task<bool> WaitForPermissionAsync(string permissionId, TimeSpan timeout, CancellationToken ct)
        {
            if (!_pendingPermissions.TryGetValue(permissionId, out var tcs))
                return false;

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(timeout);
                return await tcs.Task.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                _pendingPermissions.TryRemove(permissionId, out _);
                return false; // Auto-deny on timeout
            }
        }

        public bool RespondToPermission(string permissionId, bool approved)
        {
            if (_pendingPermissions.TryRemove(permissionId, out var tcs))
            {
                tcs.TrySetResult(approved);
                return true;
            }
            return false;
        }

        public void AddMessage(string role, string content)
        {
            Messages.Add(new ConversationMessage { Role = role, Content = content, Timestamp = DateTime.UtcNow });
            LastActivityAt = DateTime.UtcNow;
        }

        public void Dispose()
        {
            _processingCts?.Cancel();
            _processingCts?.Dispose();
            CompleteEventChannel();
            foreach (var tcs in _pendingPermissions.Values)
                tcs.TrySetCanceled();
            _pendingPermissions.Clear();
        }
    }

    public class ConversationMessage
    {
        public string Role { get; set; } = "";
        public string Content { get; set; } = "";
        public DateTime Timestamp { get; set; }
        public List<ConversationToolCall>? ToolCalls { get; set; }
    }

    public class ConversationToolCall
    {
        public string Name { get; set; } = "";
        public string Args { get; set; } = "";
        public string? Result { get; set; }
        public double? ElapsedSeconds { get; set; }
    }
}
