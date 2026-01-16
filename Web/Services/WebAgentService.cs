using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using CodingAgent;
using thuvu.Models;
using thuvu.Tools;

namespace thuvu.Web.Services
{
    /// <summary>
    /// Represents a pending permission request waiting for user response
    /// </summary>
    public class PendingPermissionRequest
    {
        public string RequestId { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public string ToolName { get; set; } = "";
        public string ArgsJson { get; set; } = "";
        public TaskCompletionSource<char> ResponseTcs { get; set; } = new();
        public DateTime RequestedAt { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// Represents a web-based agent session
    /// </summary>
    public class WebAgentSession
    {
        public string SessionId { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public List<ChatMessage> Messages { get; set; } = new();
        public List<Tool> Tools { get; set; } = new();
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime LastActivityAt { get; set; } = DateTime.Now;
        public bool IsProcessing { get; set; }
        public CancellationTokenSource? CurrentCts { get; set; }
        public ConcurrentDictionary<string, PendingPermissionRequest> PendingPermissions { get; } = new();
    }

    /// <summary>
    /// Summary of a persisted session for listing in UI.
    /// </summary>
    public class SessionSummary
    {
        public string SessionId { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public DateTime LastActivityAt { get; set; }
        public int MessageCount { get; set; }
        public bool IsActive { get; set; }
    }

    /// <summary>
    /// Service that wraps AgentLoop for web clients
    /// </summary>
    public class WebAgentService
    {
        private readonly HttpClient _http;
        private readonly ConcurrentDictionary<string, WebAgentSession> _sessions = new();
        
        // Event raised when permission is needed - subscribers should send SignalR event
        public event Action<string, PendingPermissionRequest>? OnPermissionRequired;

        public WebAgentService(HttpClient http)
        {
            _http = http;
            
            // Clear any existing handlers
            PermissionManager.CustomPermissionPrompt = null;
            PermissionManager.AsyncPermissionPrompt = null;
        }
        
        /// <summary>
        /// Gets the HttpClient for the current model. If the model has its own HostUrl/AuthToken,
        /// creates a dedicated client; otherwise uses the default shared client.
        /// </summary>
        private HttpClient GetHttpClientForCurrentModel()
        {
            var modelId = AgentConfig.Config.Model;
            Console.WriteLine($"[WebAgentService] GetHttpClientForCurrentModel: modelId={modelId}");
            AgentLogger.LogDebug("GetHttpClientForCurrentModel: modelId={ModelId}", modelId);
            
            var modelConfig = Models.ModelRegistry.Instance?.GetModel(modelId);
            Console.WriteLine($"[WebAgentService] ModelRegistry lookup: found={modelConfig != null}, HostUrl={modelConfig?.HostUrl ?? "null"}");
            AgentLogger.LogDebug("ModelRegistry lookup: found={Found}, HostUrl={HostUrl}", modelConfig != null, modelConfig?.HostUrl ?? "null");
            
            if (modelConfig != null && !string.IsNullOrEmpty(modelConfig.HostUrl))
            {
                // Model has its own endpoint config - create a dedicated client
                var client = modelConfig.CreateHttpClient();
                Console.WriteLine($"[WebAgentService] Created dedicated HttpClient: BaseAddress={client.BaseAddress}, HasAuth={client.DefaultRequestHeaders.Authorization != null}");
                AgentLogger.LogInfo("Created dedicated HttpClient for model {ModelId}: BaseAddress={BaseAddress}", modelId, client.BaseAddress);
                return client;
            }
            
            // Fall back to default shared client
            Console.WriteLine($"[WebAgentService] Using shared HttpClient: BaseAddress={_http.BaseAddress}");
            AgentLogger.LogDebug("Using shared HttpClient: BaseAddress={BaseAddress}", _http.BaseAddress);
            return _http;
        }
        
        /// <summary>
        /// Set up async permission handler for a specific session.
        /// Called when starting message processing to wire up the permission callback.
        /// </summary>
        private void SetupAsyncPermissionHandler(string sessionId)
        {
            PermissionManager.AsyncPermissionPrompt = async (toolName, argsJson) =>
            {
                // Check if auto-approve is enabled
                if (AgentConfig.Config.AutoApproveTuiTools)
                {
                    SessionLogger.Instance.LogInfo($"Auto-approving tool {toolName} (auto-approve enabled)");
                    return 'S'; // Session allow
                }
                
                // Check if already in persistent permissions
                var repoPath = Path.GetFullPath(AgentConfig.GetWorkDirectory())
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var permissionKey = $"{repoPath}:{toolName}";
                
                if (AgentConfig.Config.ToolPermissions.ContainsKey(permissionKey))
                {
                    return 'A'; // Already allowed
                }
                
                // Get session and create pending request
                if (!_sessions.TryGetValue(sessionId, out var session))
                {
                    SessionLogger.Instance.LogInfo($"Session {sessionId} not found for permission request");
                    return 'N'; // Deny if session not found
                }
                
                var request = new PendingPermissionRequest
                {
                    ToolName = toolName,
                    ArgsJson = argsJson
                };
                
                session.PendingPermissions[request.RequestId] = request;
                
                try
                {
                    // Notify UI that permission is needed
                    OnPermissionRequired?.Invoke(sessionId, request);
                    
                    // Wait for response with timeout (5 minutes)
                    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                    using var linkedCts = session.CurrentCts != null 
                        ? CancellationTokenSource.CreateLinkedTokenSource(cts.Token, session.CurrentCts.Token)
                        : cts;
                    
                    linkedCts.Token.Register(() => request.ResponseTcs.TrySetResult('N'));
                    
                    var result = await request.ResponseTcs.Task;
                    return result;
                }
                finally
                {
                    session.PendingPermissions.TryRemove(request.RequestId, out _);
                }
            };
        }
        
        /// <summary>
        /// Handle permission response from UI
        /// </summary>
        public bool RespondToPermission(string sessionId, string requestId, char choice)
        {
            if (!_sessions.TryGetValue(sessionId, out var session))
                return false;
                
            if (!session.PendingPermissions.TryGetValue(requestId, out var request))
                return false;
                
            request.ResponseTcs.TrySetResult(choice);
            return true;
        }

        /// <summary>
        /// Create a new session or get existing one
        /// </summary>
        public WebAgentSession GetOrCreateSession(string? sessionId = null)
        {
            if (sessionId != null && _sessions.TryGetValue(sessionId, out var existing))
            {
                existing.LastActivityAt = DateTime.Now;
                return existing;
            }

            // Try to load from database if session ID provided
            if (sessionId != null)
            {
                var loaded = LoadSessionFromDatabaseAsync(sessionId).GetAwaiter().GetResult();
                if (loaded != null)
                {
                    _sessions[loaded.SessionId] = loaded;
                    AgentLogger.LogInfo("Restored session {SessionId} from database", sessionId);
                    return loaded;
                }
            }
            
            // If no session ID provided, try to load the most recent session from database
            if (sessionId == null)
            {
                var recentSessions = SqliteService.Instance.GetRecentSessionsAsync(1).GetAwaiter().GetResult();
                if (recentSessions.Count > 0)
                {
                    var mostRecent = recentSessions[0];
                    // Check if it's recent enough (e.g., within the last 24 hours)
                    if (mostRecent.LastActivityAt > DateTime.Now.AddHours(-24))
                    {
                        var loaded = LoadSessionFromDatabaseAsync(mostRecent.SessionId).GetAwaiter().GetResult();
                        if (loaded != null)
                        {
                            _sessions[loaded.SessionId] = loaded;
                            AgentLogger.LogInfo("Restored most recent session {SessionId} from database", mostRecent.SessionId);
                            return loaded;
                        }
                    }
                }
            }

            var session = new WebAgentSession
            {
                Messages = new List<ChatMessage>
                {
                    new("system", SystemPromptManager.Instance.GetCurrentSystemPrompt(McpConfig.Instance.McpModeActive))
                },
                Tools = BuildTools.GetBuildTools()
            };

            _sessions[session.SessionId] = session;
            
            // Save new session to database
            _ = SaveSessionToDatabaseAsync(session);
            
            return session;
        }

        /// <summary>
        /// Save a session to the SQLite database for persistence.
        /// </summary>
        public async Task SaveSessionToDatabaseAsync(WebAgentSession session)
        {
            try
            {
                var sessionData = new SessionData
                {
                    SessionId = session.SessionId,
                    SystemPrompt = session.Messages.FirstOrDefault(m => m.Role == "system")?.Content,
                    ModelId = AgentConfig.Config.Model,
                    AgentRole = "main",
                    Title = GenerateSessionTitle(session),
                    WorkDirectory = AgentConfig.Config.WorkDirectory,
                    CreatedAt = session.CreatedAt,
                    LastActivityAt = session.LastActivityAt
                };

                await SqliteService.Instance.SaveSessionAsync(sessionData);
            }
            catch (Exception ex)
            {
                AgentLogger.LogError("Failed to save session {SessionId}: {Error}", session.SessionId, ex.Message);
            }
        }

        /// <summary>
        /// Generate a session title from the first user message.
        /// </summary>
        private static string GenerateSessionTitle(WebAgentSession session)
        {
            var firstUserMsg = session.Messages.FirstOrDefault(m => m.Role == "user");
            if (firstUserMsg != null && !string.IsNullOrEmpty(firstUserMsg.Content))
            {
                var content = firstUserMsg.Content;
                // Truncate to first 50 chars
                if (content.Length > 50)
                    content = content[..50] + "...";
                return content;
            }
            return $"Session {session.SessionId}";
        }

        /// <summary>
        /// Load a session from the SQLite database.
        /// </summary>
        private async Task<WebAgentSession?> LoadSessionFromDatabaseAsync(string sessionId)
        {
            try
            {
                var sessionData = await SqliteService.Instance.LoadSessionAsync(sessionId);
                if (sessionData == null)
                    return null;

                // Load active messages (respecting summarization) from the messages table
                var messageRecords = await SqliteService.Instance.GetActiveSessionMessagesAsync(sessionId);
                var messages = ReconstructMessagesFromRecords(messageRecords, sessionData.SystemPrompt);

                return new WebAgentSession
                {
                    SessionId = sessionData.SessionId,
                    Messages = messages,
                    Tools = BuildTools.GetBuildTools(),
                    CreatedAt = sessionData.CreatedAt,
                    LastActivityAt = sessionData.LastActivityAt,
                    IsProcessing = false // Reset processing state on restore
                };
            }
            catch (Exception ex)
            {
                AgentLogger.LogError("Failed to load session {SessionId}: {Error}", sessionId, ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Reconstruct ChatMessage list from MessageRecord entries.
        /// Handles summarization by converting summary messages to context.
        /// </summary>
        private static List<ChatMessage> ReconstructMessagesFromRecords(List<MessageRecord> records, string? systemPrompt)
        {
            var messages = new List<ChatMessage>();
            
            // Always add system prompt first
            messages.Add(new ChatMessage("system", 
                systemPrompt ?? SystemPromptManager.Instance.GetCurrentSystemPrompt(McpConfig.Instance.McpModeActive)));
            
            // Group records by depth 0 (main agent messages only for context reconstruction)
            foreach (var record in records.Where(r => r.AgentDepth == 0))
            {
                switch (record.MessageType)
                {
                    case "summary":
                        // Convert summary to context messages (same format as SummarizeConversationAsync output)
                        if (!string.IsNullOrEmpty(record.ResponseContent))
                        {
                            messages.Add(new ChatMessage("user", $"[CONVERSATION SUMMARY - Context from previous messages]\n{record.ResponseContent}\n\n[END SUMMARY - Continue from here]"));
                            messages.Add(new ChatMessage("assistant", "I understand. I have the context from the summarized conversation. I'll continue from where we left off."));
                        }
                        break;
                    case "user":
                        if (!string.IsNullOrEmpty(record.RequestContent))
                            messages.Add(new ChatMessage("user", record.RequestContent));
                        break;
                    case "assistant":
                        if (!string.IsNullOrEmpty(record.ResponseContent))
                            messages.Add(new ChatMessage("assistant", record.ResponseContent));
                        break;
                    case "tool_call":
                        // Tool calls are typically paired with their results, handle as assistant message if it has content
                        if (!string.IsNullOrEmpty(record.ResponseContent))
                            messages.Add(new ChatMessage("assistant", record.ResponseContent));
                        break;
                    case "tool_result":
                        if (!string.IsNullOrEmpty(record.ToolResultJson))
                            messages.Add(new ChatMessage("tool", record.ToolResultJson) { Name = record.ToolName });
                        break;
                }
            }
            
            return messages;
        }

        /// <summary>
        /// Record a user message to the database.
        /// </summary>
        public async Task<long> RecordUserMessageAsync(string sessionId, string content)
        {
            var record = new MessageRecord
            {
                SessionId = sessionId,
                StartedAt = DateTime.Now,
                AgentRole = "main",
                AgentDepth = 0,
                ModelId = AgentConfig.Config.Model,
                MessageType = "user",
                RequestContent = content,
                Status = "completed"
            };
            
            var messageId = await SqliteService.Instance.StartMessageAsync(record);
            
            // Immediately complete user messages
            await SqliteService.Instance.CompleteMessageAsync(messageId, new MessageCompleteInfo
            {
                CompletedAt = DateTime.Now,
                DurationMs = 0
            });
            
            return messageId;
        }

        /// <summary>
        /// Start recording an assistant message (call when sending to LLM).
        /// </summary>
        public async Task<long> StartAssistantMessageAsync(string sessionId, int? contextTokens = null)
        {
            var record = new MessageRecord
            {
                SessionId = sessionId,
                StartedAt = DateTime.Now,
                AgentRole = "main",
                AgentDepth = 0,
                ModelId = AgentConfig.Config.Model,
                MessageType = "assistant",
                ContextMode = "full",
                ContextTokenCount = contextTokens,
                Status = "running"
            };
            
            return await SqliteService.Instance.StartMessageAsync(record);
        }

        /// <summary>
        /// Complete an assistant message with response data.
        /// </summary>
        public async Task CompleteAssistantMessageAsync(long messageId, string response, int? promptTokens = null, int? completionTokens = null, long durationMs = 0)
        {
            await SqliteService.Instance.CompleteMessageAsync(messageId, new MessageCompleteInfo
            {
                CompletedAt = DateTime.Now,
                DurationMs = durationMs,
                ResponseContent = response,
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
                TotalTokens = (promptTokens ?? 0) + (completionTokens ?? 0)
            });
        }

        /// <summary>
        /// Record a tool call.
        /// </summary>
        public async Task<long> RecordToolCallAsync(string sessionId, string toolName, string argsJson, long? parentMessageId = null)
        {
            var record = new MessageRecord
            {
                SessionId = sessionId,
                ParentMessageId = parentMessageId,
                StartedAt = DateTime.Now,
                AgentRole = "main",
                AgentDepth = 0,
                ModelId = AgentConfig.Config.Model,
                MessageType = "tool_call",
                ToolName = toolName,
                ToolArgsJson = argsJson,
                Status = "running"
            };
            
            return await SqliteService.Instance.StartMessageAsync(record);
        }

        /// <summary>
        /// Complete a tool call with result.
        /// </summary>
        public async Task CompleteToolCallAsync(long messageId, string resultJson, long durationMs = 0)
        {
            await SqliteService.Instance.CompleteMessageAsync(messageId, new MessageCompleteInfo
            {
                CompletedAt = DateTime.Now,
                DurationMs = durationMs,
                ToolResultJson = resultJson
            });
        }

        /// <summary>
        /// Get list of recent sessions for the UI.
        /// </summary>
        public async Task<List<SessionSummary>> GetRecentSessionsAsync(int limit = 10)
        {
            try
            {
                var sessions = await SqliteService.Instance.GetRecentSessionsAsync(limit);
                return sessions.Select(s => new SessionSummary
                {
                    SessionId = s.SessionId,
                    CreatedAt = s.CreatedAt,
                    LastActivityAt = s.LastActivityAt,
                    MessageCount = s.MessageCount,
                    IsActive = _sessions.ContainsKey(s.SessionId)
                }).ToList();
            }
            catch (Exception ex)
            {
                AgentLogger.LogError("Failed to get recent sessions: {Error}", ex.Message);
                return new List<SessionSummary>();
            }
        }

        /// <summary>
        /// Get session by ID
        /// </summary>
        public WebAgentSession? GetSession(string sessionId)
        {
            return _sessions.TryGetValue(sessionId, out var session) ? session : null;
        }

        /// <summary>
        /// Get session history for UI display from the database.
        /// Returns messages formatted for the Chat component.
        /// </summary>
        public async Task<List<ChatMessageDisplayDto>> GetSessionHistoryAsync(string sessionId)
        {
            var result = new List<ChatMessageDisplayDto>();
            
            try
            {
                var messages = await SqliteService.Instance.GetSessionMessagesAsync(sessionId);
                
                foreach (var msg in messages)
                {
                    // Skip system messages - they're internal
                    if (msg.MessageType == "system")
                        continue;
                    
                    var displayMsg = new ChatMessageDisplayDto
                    {
                        Role = MapMessageTypeToRole(msg.MessageType),
                        Content = GetMessageContent(msg),
                        ToolName = msg.ToolName,
                        ToolArgs = msg.ToolArgsJson,
                        Timestamp = msg.StartedAt,
                        AgentDepth = msg.AgentDepth,
                        AgentRole = msg.AgentRole,
                        IsSubAgentDelegation = msg.MessageType == "delegation"
                    };
                    
                    result.Add(displayMsg);
                }
            }
            catch (Exception ex)
            {
                AgentLogger.LogError("Failed to get session history: {Error}", ex.Message);
            }
            
            return result;
        }

        /// <summary>
        /// Map database message type to display role
        /// </summary>
        private static string MapMessageTypeToRole(string? messageType)
        {
            return messageType switch
            {
                "user" => "user",
                "assistant" => "assistant",
                "tool_call" => "tool",
                "tool_result" => "tool",
                "delegation" => "tool",
                _ => "system"
            };
        }

        /// <summary>
        /// Get the appropriate content to display for a message
        /// </summary>
        private static string GetMessageContent(MessageRecord msg)
        {
            return msg.MessageType switch
            {
                "user" => msg.RequestContent ?? "",
                "assistant" => msg.ResponseContent ?? "",
                "tool_call" => msg.ToolArgsJson ?? "",
                "tool_result" => msg.ToolResultJson ?? "",
                "delegation" => msg.ResponseSummary ?? msg.ResponseContent ?? "",
                _ => msg.ResponseContent ?? msg.RequestContent ?? ""
            };
        }

        /// <summary>
        /// Send a message and stream the response
        /// </summary>
        public async IAsyncEnumerable<AgentStreamEvent> SendMessageAsync(
            string sessionId,
            string message,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var session = GetSession(sessionId);
            if (session == null)
            {
                yield return new AgentStreamEvent { Type = "error", Data = "Session not found" };
                yield break;
            }

            if (session.IsProcessing)
            {
                yield return new AgentStreamEvent { Type = "error", Data = "Session is already processing a request" };
                yield break;
            }

            session.IsProcessing = true;
            session.CurrentCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var linkedCt = session.CurrentCts.Token;
            
            // Set session as active in database
            _ = SqliteService.Instance.SetSessionActiveAsync(sessionId, true, linkedCt);
            
            // Set up async permission handler for this session
            SetupAsyncPermissionHandler(sessionId);
            
            // Declare handler outside try block so we can unsubscribe in finally
            Action<string, PendingPermissionRequest>? permissionHandler = null;
            System.Threading.Channels.ChannelWriter<AgentStreamEvent>? channelWriter = null;
            
            // Track pending tool calls (name -> args) for combining with results
            var pendingToolCalls = new System.Collections.Concurrent.ConcurrentDictionary<string, (string ArgsJson, DateTime StartedAt)>();
            
            // Track token usage for final message
            int? lastPromptTokens = null;
            int? lastCompletionTokens = null;
            int? lastTotalTokens = null;

            try
            {
                // Process @file references - expand file contents inline
                var processedMessage = await ProcessFileReferencesAsync(message);
                
                session.Messages.Add(new ChatMessage("user", processedMessage));
                session.LastActivityAt = DateTime.Now;
                
                // Record user message to database immediately
                await RecordUserMessageAsync(sessionId, processedMessage);

                yield return new AgentStreamEvent { Type = "status", Data = "Processing..." };

                // Use a channel to collect events from callbacks
                var channel = System.Threading.Channels.Channel.CreateUnbounded<AgentStreamEvent>();
                var writer = channel.Writer;
                channelWriter = writer;
                
                // Subscribe to permission requests for this processing run
                permissionHandler = (sid, request) =>
                {
                    if (sid == sessionId)
                    {
                        writer.TryWrite(new AgentStreamEvent 
                        { 
                            Type = "permission_request",
                            ToolName = request.ToolName,
                            Data = JsonSerializer.Serialize(new 
                            {
                                requestId = request.RequestId,
                                toolName = request.ToolName,
                                args = request.ArgsJson
                            })
                        });
                    }
                };
                OnPermissionRequired += permissionHandler;

                var completionTask = Task.Run(async () =>
                {
                    try
                    {
                        // Get the appropriate HttpClient for the current model
                        var httpClient = GetHttpClientForCurrentModel();
                        
                        var result = await AgentLoop.CompleteWithToolsStreamingAsync(
                            httpClient,
                            AgentConfig.Config.Model,
                            session.Messages,
                            session.Tools,
                            linkedCt,
                            onToken: token =>
                            {
                                writer.TryWrite(new AgentStreamEvent { Type = "token", Data = token });
                            },
                            onToolResult: (name, result) =>
                            {
                                writer.TryWrite(new AgentStreamEvent 
                                { 
                                    Type = "tool_result", 
                                    ToolName = name,
                                    Data = TruncateResult(result)
                                });
                            },
                            onUsage: usage =>
                            {
                                writer.TryWrite(new AgentStreamEvent 
                                { 
                                    Type = "usage",
                                    Data = JsonSerializer.Serialize(new 
                                    { 
                                        prompt = usage.PromptTokens,
                                        completion = usage.CompletionTokens,
                                        total = usage.TotalTokens
                                    })
                                });
                                
                                // Update TokenTracker first, then send context usage info
                                var tokenTracker = TokenTracker.Instance;
                                tokenTracker.UpdateFromUsage(usage);
                                
                                writer.TryWrite(new AgentStreamEvent 
                                { 
                                    Type = "context_usage",
                                    Data = JsonSerializer.Serialize(new 
                                    { 
                                        totalTokens = tokenTracker.TotalTokens,
                                        maxContextLength = tokenTracker.MaxContextLength,
                                        usagePercent = tokenTracker.UsagePercent * 100
                                    })
                                });
                                
                                // Track token usage for the final assistant message
                                lastPromptTokens = usage.PromptTokens;
                                lastCompletionTokens = usage.CompletionTokens;
                                lastTotalTokens = usage.TotalTokens;
                            },
                            onToolComplete: (name, argsJson, toolResult, elapsed) =>
                            {
                                writer.TryWrite(new AgentStreamEvent 
                                { 
                                    Type = "tool_complete",
                                    ToolName = name,
                                    Data = $"Completed in {elapsed.TotalSeconds:F1}s"
                                });
                                
                                // Record tool call + result to database immediately
                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        var toolRecord = new MessageRecord
                                        {
                                            SessionId = sessionId,
                                            StartedAt = DateTime.Now.AddMilliseconds(-elapsed.TotalMilliseconds),
                                            AgentRole = "main",
                                            AgentDepth = 0,
                                            ModelId = AgentConfig.Config.Model,
                                            MessageType = "tool_call",
                                            ToolName = name,
                                            ToolArgsJson = argsJson,
                                            ToolResultJson = TruncateForDb(toolResult),
                                            Status = "completed"
                                        };
                                        await SqliteService.Instance.StartMessageAsync(toolRecord);
                                    }
                                    catch (Exception ex)
                                    {
                                        AgentLogger.LogError("Failed to record tool call: {Error}", ex.Message);
                                    }
                                });
                            },
                            onToolProgress: progress =>
                            {
                                // Send tool_start when status is Running and just started
                                if (progress.Status == ToolStatus.Running || progress.Status == ToolStatus.Pending)
                                {
                                    writer.TryWrite(new AgentStreamEvent 
                                    { 
                                        Type = "tool_progress",
                                        ToolName = progress.ToolName,
                                        Data = progress.Message ?? $"{progress.Status} ({progress.ElapsedFormatted})"
                                    });
                                }
                                else
                                {
                                    writer.TryWrite(new AgentStreamEvent 
                                    { 
                                        Type = "tool_progress",
                                        ToolName = progress.ToolName,
                                        Data = progress.Message ?? progress.Status.ToString()
                                    });
                                }
                            },
                            onToolCall: (name, argsJson) =>
                            {
                                writer.TryWrite(new AgentStreamEvent 
                                { 
                                    Type = "tool_call",
                                    ToolName = name,
                                    Data = argsJson
                                });
                            }
                        );

                        if (result != null)
                        {
                            session.Messages.Add(new ChatMessage("assistant", result));
                        }

                        // Check if auto-summarization is needed
                        var tracker = TokenTracker.Instance;
                        if (tracker.AutoSummarizeEnabled && tracker.UsagePercent >= 0.90)
                        {
                            writer.TryWrite(new AgentStreamEvent 
                            { 
                                Type = "context_summarizing",
                                Data = $"Context at {tracker.UsagePercent:P0}, auto-summarizing..."
                            });

                            var summarized = await AgentLoop.HandleContextSizeAsync(
                                httpClient,
                                AgentConfig.Config.Model,
                                session.Messages,
                                linkedCt,
                                status => writer.TryWrite(new AgentStreamEvent { Type = "status", Data = status }),
                                onSummarized: async (summaryContent) =>
                                {
                                    // Record summarization to database
                                    var messageIds = await SqliteService.Instance.GetNonSummarizedMessageIdsAsync(sessionId);
                                    if (messageIds.Count > 0)
                                    {
                                        await SqliteService.Instance.RecordSummarizationAsync(sessionId, summaryContent, messageIds);
                                        AgentLogger.LogInfo("Recorded summarization for session {SessionId}, {Count} messages summarized", sessionId, messageIds.Count);
                                    }
                                }
                            );

                            if (summarized)
                            {
                                // Reset token tracker after summarization
                                tracker.Reset();
                                
                                // Send updated context usage
                                writer.TryWrite(new AgentStreamEvent 
                                { 
                                    Type = "context_usage",
                                    Data = JsonSerializer.Serialize(new 
                                    { 
                                        totalTokens = tracker.TotalTokens,
                                        maxContextLength = tracker.MaxContextLength,
                                        usagePercent = tracker.UsagePercent * 100
                                    })
                                });

                                writer.TryWrite(new AgentStreamEvent 
                                { 
                                    Type = "context_summarized",
                                    Data = "Conversation summarized to reduce context size"
                                });
                            }
                        }

                        // Record assistant response to database with token usage
                        if (!string.IsNullOrEmpty(result))
                        {
                            try
                            {
                                var msgRecord = new MessageRecord
                                {
                                    SessionId = sessionId,
                                    StartedAt = DateTime.Now,
                                    AgentRole = "main",
                                    AgentDepth = 0,
                                    ModelId = AgentConfig.Config.Model,
                                    MessageType = "assistant",
                                    ResponseContent = result,
                                    PromptTokens = lastPromptTokens,
                                    CompletionTokens = lastCompletionTokens,
                                    TotalTokens = lastTotalTokens,
                                    Status = "completed"
                                };
                                await SqliteService.Instance.StartMessageAsync(msgRecord, linkedCt);
                            }
                            catch (Exception dbEx)
                            {
                                AgentLogger.LogError("Failed to record assistant message: {Error}", dbEx.Message);
                            }
                        }

                        writer.TryWrite(new AgentStreamEvent { Type = "done", Data = result ?? "" });
                    }
                    catch (OperationCanceledException)
                    {
                        writer.TryWrite(new AgentStreamEvent { Type = "cancelled", Data = "Request cancelled" });
                        
                        // Record cancellation
                        _ = SqliteService.Instance.SetSessionActiveAsync(sessionId, false);
                    }
                    catch (Exception ex)
                    {
                        writer.TryWrite(new AgentStreamEvent { Type = "error", Data = ex.Message });
                        
                        // Record error
                        _ = SqliteService.Instance.SetSessionActiveAsync(sessionId, false);
                    }
                    finally
                    {
                        writer.Complete();
                    }
                }, linkedCt);

                // Stream events as they come
                await foreach (var evt in channel.Reader.ReadAllAsync(linkedCt))
                {
                    yield return evt;
                }

                await completionTask;
            }
            finally
            {
                if (permissionHandler != null)
                    OnPermissionRequired -= permissionHandler;
                session.IsProcessing = false;
                session.CurrentCts?.Dispose();
                session.CurrentCts = null;
                
                // Set session as inactive in database
                _ = SqliteService.Instance.SetSessionActiveAsync(sessionId, false);
                
                // Save session metadata
                _ = SaveSessionToDatabaseAsync(session);
            }
        }

        /// <summary>
        /// Core message processing - runs the agent loop without adding a user message.
        /// Used when the caller has already added the user message (e.g., for multimodal messages).
        /// </summary>
        private async IAsyncEnumerable<AgentStreamEvent> ProcessMessageCoreAsync(
            string sessionId,
            WebAgentSession session,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            session.IsProcessing = true;
            session.CurrentCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var linkedCt = session.CurrentCts.Token;
            
            // Set session as active in database
            _ = SqliteService.Instance.SetSessionActiveAsync(sessionId, true, linkedCt);
            
            // Set up async permission handler for this session
            SetupAsyncPermissionHandler(sessionId);
            
            // Declare handler outside try block so we can unsubscribe in finally
            Action<string, PendingPermissionRequest>? permissionHandler = null;
            System.Threading.Channels.ChannelWriter<AgentStreamEvent>? channelWriter = null;
            
            // Track token usage for final message
            int? lastPromptTokens = null;
            int? lastCompletionTokens = null;
            int? lastTotalTokens = null;

            try
            {
                yield return new AgentStreamEvent { Type = "status", Data = "Processing..." };

                // Use a channel to collect events from callbacks
                var channel = System.Threading.Channels.Channel.CreateUnbounded<AgentStreamEvent>();
                var writer = channel.Writer;
                channelWriter = writer;
                
                // Subscribe to permission requests for this processing run
                permissionHandler = (sid, request) =>
                {
                    if (sid == sessionId)
                    {
                        writer.TryWrite(new AgentStreamEvent 
                        { 
                            Type = "permission_request",
                            ToolName = request.ToolName,
                            Data = JsonSerializer.Serialize(new 
                            {
                                requestId = request.RequestId,
                                toolName = request.ToolName,
                                args = request.ArgsJson
                            })
                        });
                    }
                };
                OnPermissionRequired += permissionHandler;

                var completionTask = Task.Run(async () =>
                {
                    try
                    {
                        // Get the appropriate HttpClient for the current model
                        var httpClient = GetHttpClientForCurrentModel();
                        
                        var result = await AgentLoop.CompleteWithToolsStreamingAsync(
                            httpClient,
                            AgentConfig.Config.Model,
                            session.Messages,
                            session.Tools,
                            linkedCt,
                            onToken: token =>
                            {
                                writer.TryWrite(new AgentStreamEvent { Type = "token", Data = token });
                            },
                            onToolResult: (name, result) =>
                            {
                                writer.TryWrite(new AgentStreamEvent 
                                { 
                                    Type = "tool_result", 
                                    ToolName = name,
                                    Data = TruncateResult(result)
                                });
                            },
                            onUsage: usage =>
                            {
                                writer.TryWrite(new AgentStreamEvent 
                                { 
                                    Type = "usage",
                                    Data = JsonSerializer.Serialize(new 
                                    { 
                                        prompt = usage.PromptTokens,
                                        completion = usage.CompletionTokens,
                                        total = usage.TotalTokens
                                    })
                                });
                                
                                // Update TokenTracker first, then send context usage info
                                var tokenTracker = TokenTracker.Instance;
                                tokenTracker.UpdateFromUsage(usage);
                                
                                writer.TryWrite(new AgentStreamEvent 
                                { 
                                    Type = "context_usage",
                                    Data = JsonSerializer.Serialize(new 
                                    { 
                                        totalTokens = tokenTracker.TotalTokens,
                                        maxContextLength = tokenTracker.MaxContextLength,
                                        usagePercent = tokenTracker.UsagePercent * 100
                                    })
                                });
                                
                                // Track token usage for the final assistant message
                                lastPromptTokens = usage.PromptTokens;
                                lastCompletionTokens = usage.CompletionTokens;
                                lastTotalTokens = usage.TotalTokens;
                            },
                            onToolComplete: (name, argsJson, toolResult, elapsed) =>
                            {
                                writer.TryWrite(new AgentStreamEvent 
                                { 
                                    Type = "tool_complete",
                                    ToolName = name,
                                    Data = $"Completed in {elapsed.TotalSeconds:F1}s"
                                });
                                
                                // Record tool call + result to database immediately
                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        var toolRecord = new MessageRecord
                                        {
                                            SessionId = sessionId,
                                            StartedAt = DateTime.Now.AddMilliseconds(-elapsed.TotalMilliseconds),
                                            AgentRole = "main",
                                            AgentDepth = 0,
                                            ModelId = AgentConfig.Config.Model,
                                            MessageType = "tool_call",
                                            ToolName = name,
                                            ToolArgsJson = argsJson,
                                            ToolResultJson = TruncateForDb(toolResult),
                                            Status = "completed"
                                        };
                                        await SqliteService.Instance.StartMessageAsync(toolRecord);
                                    }
                                    catch (Exception ex)
                                    {
                                        AgentLogger.LogError("Failed to record tool call: {Error}", ex.Message);
                                    }
                                });
                            },
                            onToolProgress: progress =>
                            {
                                // Send tool_start when status is Running and just started
                                if (progress.Status == ToolStatus.Running || progress.Status == ToolStatus.Pending)
                                {
                                    writer.TryWrite(new AgentStreamEvent 
                                    { 
                                        Type = "tool_progress",
                                        ToolName = progress.ToolName,
                                        Data = progress.Message ?? $"{progress.Status} ({progress.ElapsedFormatted})"
                                    });
                                }
                                else
                                {
                                    writer.TryWrite(new AgentStreamEvent 
                                    { 
                                        Type = "tool_progress",
                                        ToolName = progress.ToolName,
                                        Data = progress.Message ?? progress.Status.ToString()
                                    });
                                }
                            },
                            onToolCall: (name, argsJson) =>
                            {
                                writer.TryWrite(new AgentStreamEvent 
                                { 
                                    Type = "tool_call",
                                    ToolName = name,
                                    Data = argsJson
                                });
                            }
                        );

                        if (result != null)
                        {
                            session.Messages.Add(new ChatMessage("assistant", result));
                        }

                        // Record assistant response to database with token usage
                        if (!string.IsNullOrEmpty(result))
                        {
                            try
                            {
                                var msgRecord = new MessageRecord
                                {
                                    SessionId = sessionId,
                                    StartedAt = DateTime.Now,
                                    AgentRole = "main",
                                    AgentDepth = 0,
                                    ModelId = AgentConfig.Config.Model,
                                    MessageType = "assistant",
                                    ResponseContent = result,
                                    PromptTokens = lastPromptTokens,
                                    CompletionTokens = lastCompletionTokens,
                                    TotalTokens = lastTotalTokens,
                                    Status = "completed"
                                };
                                await SqliteService.Instance.StartMessageAsync(msgRecord, linkedCt);
                            }
                            catch (Exception dbEx)
                            {
                                AgentLogger.LogError("Failed to record assistant message: {Error}", dbEx.Message);
                            }
                        }

                        writer.TryWrite(new AgentStreamEvent { Type = "done", Data = result ?? "" });
                    }
                    catch (OperationCanceledException)
                    {
                        writer.TryWrite(new AgentStreamEvent { Type = "cancelled", Data = "Request cancelled" });
                        
                        // Record cancellation
                        _ = SqliteService.Instance.SetSessionActiveAsync(sessionId, false);
                    }
                    catch (Exception ex)
                    {
                        writer.TryWrite(new AgentStreamEvent { Type = "error", Data = ex.Message });
                        
                        // Record error
                        _ = SqliteService.Instance.SetSessionActiveAsync(sessionId, false);
                    }
                    finally
                    {
                        writer.Complete();
                    }
                }, linkedCt);

                // Stream events as they come
                await foreach (var evt in channel.Reader.ReadAllAsync(linkedCt))
                {
                    yield return evt;
                }

                await completionTask;
            }
            finally
            {
                if (permissionHandler != null)
                    OnPermissionRequired -= permissionHandler;
                session.IsProcessing = false;
                session.CurrentCts?.Dispose();
                session.CurrentCts = null;
                
                // Set session as inactive in database
                _ = SqliteService.Instance.SetSessionActiveAsync(sessionId, false);
                
                // Save session metadata
                _ = SaveSessionToDatabaseAsync(session);
            }
        }

        /// <summary>
        /// Send a message with an image and stream the response
        /// </summary>
        public async IAsyncEnumerable<AgentStreamEvent> SendMessageWithImageAsync(
            string sessionId,
            string message,
            string imageBase64,
            string imageMimeType,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var session = GetSession(sessionId);
            if (session == null)
            {
                yield return new AgentStreamEvent { Type = "error", Data = "Session not found" };
                yield break;
            }

            if (session.IsProcessing)
            {
                yield return new AgentStreamEvent { Type = "error", Data = "Session is already processing a request" };
                yield break;
            }

            // Check vision model early before setting processing state
            var visionModel = Models.ModelRegistry.Instance.GetVisionModel();
            if (visionModel == null)
            {
                yield return new AgentStreamEvent { Type = "error", Data = "No vision model configured. Add a model with SupportsVision: true and set VisionModelId in appsettings.json" };
                yield break;
            }

            // If vision model supports tools, use the full agent loop with tool calling
            // This allows the agent to analyze the image AND take action (e.g., edit files)
            if (visionModel.SupportsTools)
            {
                // Add the multimodal message directly to session
                var userPrompt = string.IsNullOrWhiteSpace(message) 
                    ? "Please analyze the attached image and describe what you see."
                    : message;
                var userMessage = ChatMessage.CreateWithImage("user", userPrompt, imageBase64, imageMimeType);
                session.Messages.Add(userMessage);
                session.LastActivityAt = DateTime.Now;
                
                // Temporarily switch to the vision model for this request
                var originalModel = AgentConfig.Config.Model;
                var originalMaxContext = TokenTracker.Instance.MaxContextLength;
                AgentConfig.Config.Model = visionModel.ModelId;
                
                // Update TokenTracker with vision model's context length
                if (visionModel.MaxContextLength > 0)
                {
                    TokenTracker.Instance.MaxContextLength = visionModel.MaxContextLength;
                }
                
                // Record user message to database
                await RecordUserMessageAsync(sessionId, $"[Image attached] {userPrompt}");
                
                // Use the core message processing (without adding another user message)
                await foreach (var evt in ProcessMessageCoreAsync(sessionId, session, ct))
                {
                    yield return evt;
                }
                
                // Restore original model and context length
                AgentConfig.Config.Model = originalModel;
                TokenTracker.Instance.MaxContextLength = originalMaxContext;
                yield break;
            }
            
            // Fall back to vision-only mode (no tool calling)
            // This is for vision models that don't support tool calling
            session.IsProcessing = true;
            session.CurrentCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var linkedCt = session.CurrentCts.Token;

            // Use channel pattern to avoid yield in try-catch
            var channel = System.Threading.Channels.Channel.CreateUnbounded<AgentStreamEvent>();
            var writer = channel.Writer;

            var completionTask = Task.Run(async () =>
            {
                try
                {
                    writer.TryWrite(new AgentStreamEvent { Type = "status", Data = $"Analyzing image with {visionModel.DisplayName}..." });

                    var userPrompt = string.IsNullOrWhiteSpace(message) 
                        ? "Please analyze the attached image and describe what you see."
                        : message;
                    
                    // Add the multimodal message to conversation history BEFORE calling the API
                    // This way the image becomes part of the conversation
                    var userMessage = ChatMessage.CreateWithImage("user", userPrompt, imageBase64, imageMimeType);
                    session.Messages.Add(userMessage);
                    
                    // Call vision model WITH full conversation context
                    var visionResult = await Tools.VisionToolImpl.AnalyzeImageWithContextAsync(
                        session.Messages,
                        imageBase64,
                        imageMimeType,
                        userPrompt,
                        linkedCt);
                    
                    if (visionResult.Success)
                    {
                        // Add the assistant response to conversation
                        session.Messages.Add(new ChatMessage("assistant", visionResult.Description));
                        
                        // Update last activity
                        session.LastActivityAt = DateTime.Now;
                        
                        // Stream the result token by token for consistency
                        foreach (var token in (visionResult.Description ?? "").Split(' '))
                        {
                            writer.TryWrite(new AgentStreamEvent { Type = "token", Data = token + " " });
                            await Task.Delay(10, linkedCt); // Small delay for visual effect
                        }
                        
                        writer.TryWrite(new AgentStreamEvent { Type = "done", Data = visionResult.Description ?? "" });
                    }
                    else
                    {
                        // Remove the failed user message from history
                        session.Messages.RemoveAt(session.Messages.Count - 1);
                        writer.TryWrite(new AgentStreamEvent { Type = "error", Data = visionResult.Error ?? "Vision analysis failed" });
                    }
                }
                catch (OperationCanceledException)
                {
                    writer.TryWrite(new AgentStreamEvent { Type = "cancelled", Data = "Request cancelled" });
                }
                catch (Exception ex)
                {
                    writer.TryWrite(new AgentStreamEvent { Type = "error", Data = ex.Message });
                }
                finally
                {
                    writer.Complete();
                }
            }, linkedCt);

            // Stream events as they come
            await foreach (var evt in channel.Reader.ReadAllAsync(linkedCt))
            {
                yield return evt;
            }

            await completionTask;

            // Cleanup
            session.IsProcessing = false;
            session.CurrentCts?.Dispose();
            session.CurrentCts = null;
            
            // Save session to database after processing completes
            _ = SaveSessionToDatabaseAsync(session);
        }

        /// <summary>
        /// Cancel the current request for a session
        /// </summary>
        public bool CancelRequest(string sessionId)
        {
            var session = GetSession(sessionId);
            if (session?.CurrentCts != null && !session.CurrentCts.IsCancellationRequested)
            {
                session.CurrentCts.Cancel();
                return true;
            }
            return false;
        }

        /// <summary>
        /// Clear session history
        /// </summary>
        public void ClearSession(string sessionId)
        {
            var session = GetSession(sessionId);
            if (session != null)
            {
                session.Messages.Clear();
                session.Messages.Add(new ChatMessage("system", SystemPromptManager.Instance.GetCurrentSystemPrompt(McpConfig.Instance.McpModeActive)));
            }
        }

        /// <summary>
        /// Execute a slash command
        /// </summary>
        public async IAsyncEnumerable<AgentStreamEvent> ExecuteCommandAsync(
            string sessionId,
            string command,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var session = GetSession(sessionId);
            if (session == null)
            {
                yield return new AgentStreamEvent { Type = "error", Data = "Session not found" };
                yield break;
            }

            var parts = command.Split(' ', 2);
            var cmdName = parts[0].ToLowerInvariant();

            // Special handling for orchestration - needs real-time streaming
            if (cmdName == "/orchestrate")
            {
                await foreach (var evt in ExecuteOrchestrationAsync(command, ct))
                {
                    yield return evt;
                }
                yield return new AgentStreamEvent { Type = "done", Data = "" };
                yield break;
            }

            // Use a list to collect events since we can't yield in try/catch
            var events = new List<AgentStreamEvent>();
            
            await ProcessCommandAsync(sessionId, command, cmdName, session, events, ct);
            
            foreach (var evt in events)
            {
                yield return evt;
            }
            
            yield return new AgentStreamEvent { Type = "done", Data = "" };
        }
        
        /// <summary>
        /// Execute orchestration with real-time streaming
        /// </summary>
        private async IAsyncEnumerable<AgentStreamEvent> ExecuteOrchestrationAsync(
            string command,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            // Parse orchestrate options
            var orchArgs = command.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();
            var agentCount = 2;
            var retryFailed = false;
            var skipFailed = false;
            
            for (int i = 0; i < orchArgs.Count; i++)
            {
                if (orchArgs[i] == "--agents" && i + 1 < orchArgs.Count && int.TryParse(orchArgs[i + 1], out var n))
                    agentCount = Math.Clamp(n, 1, 4);
                else if (orchArgs[i] == "--retry")
                    retryFailed = true;
                else if (orchArgs[i] == "--skip")
                    skipFailed = true;
            }
            
            yield return new AgentStreamEvent { Type = "orchestration_start", Data = agentCount.ToString() };
            
            var workDir = AgentConfig.GetWorkDirectory();
            var planPath = Path.Combine(workDir, "current-plan.json");
            
            if (!File.Exists(planPath))
            {
                yield return new AgentStreamEvent { Type = "error", Data = "No plan found. Use `/plan <task>` first." };
                yield break;
            }
            
            TaskPlan? plan = null;
            string? loadError = null;
            
            try
            {
                var planJson = await File.ReadAllTextAsync(planPath, ct);
                plan = JsonSerializer.Deserialize<TaskPlan>(planJson);
            }
            catch (Exception ex)
            {
                loadError = ex.Message;
            }
            
            if (loadError != null)
            {
                yield return new AgentStreamEvent { Type = "error", Data = $"Failed to load plan: {loadError}" };
                yield break;
            }
            
            if (plan == null || plan.SubTasks.Count == 0)
            {
                yield return new AgentStreamEvent { Type = "error", Data = "Invalid or empty plan." };
                yield break;
            }
            
            // Send plan info
            yield return new AgentStreamEvent 
            { 
                Type = "orchestration_plan", 
                Data = JsonSerializer.Serialize(new 
                {
                    taskId = plan.TaskId,
                    taskName = plan.OriginalRequest,
                    subtasks = plan.SubTasks.Select(t => new 
                    {
                        id = t.Id,
                        title = t.Title,
                        description = t.Description,
                        status = t.Status.ToString()
                    }).ToList()
                })
            };
            
            yield return new AgentStreamEvent { Type = "status", Data = $"Starting orchestration with {agentCount} agent(s)..." };
            
            // Use channel for streaming events from orchestrator
            var channel = System.Threading.Channels.Channel.CreateUnbounded<AgentStreamEvent>();
            var writer = channel.Writer;
            
            var config = new OrchestratorConfig 
            { 
                MaxAgents = agentCount,
                UseProcessIsolation = false
            };
            
            var orchestrationTask = Task.Run(async () =>
            {
                try
                {
                    using var orchestrator = new TaskOrchestrator(_http, config, workDir);
                    
                    // Track which agent is working on which task
                    var agentTaskMap = new ConcurrentDictionary<string, string>();
                    
                    // Wire up events
                    orchestrator.OnAgentStarted += (agentId, taskId) =>
                    {
                        agentTaskMap[agentId] = taskId;
                        writer.TryWrite(new AgentStreamEvent 
                        { 
                            Type = "agent_started", 
                            Data = JsonSerializer.Serialize(new { agentId, taskId })
                        });
                    };
                    
                    orchestrator.OnAgentOutput += (agentId, text) =>
                    {
                        var taskId = agentTaskMap.GetValueOrDefault(agentId, "");
                        writer.TryWrite(new AgentStreamEvent 
                        { 
                            Type = "agent_output", 
                            Data = JsonSerializer.Serialize(new { agentId, taskId, text })
                        });
                    };
                    
                    orchestrator.OnAgentToolCall += (agentId, toolName, status) =>
                    {
                        var taskId = agentTaskMap.GetValueOrDefault(agentId, "");
                        writer.TryWrite(new AgentStreamEvent 
                        { 
                            Type = "agent_tool_call", 
                            Data = JsonSerializer.Serialize(new { agentId, taskId, toolName, status })
                        });
                    };
                    
                    orchestrator.OnTaskCompleted += (agentId, result) =>
                    {
                        agentTaskMap.TryRemove(agentId, out _);
                        writer.TryWrite(new AgentStreamEvent 
                        { 
                            Type = "agent_completed", 
                            Data = JsonSerializer.Serialize(new 
                            { 
                                agentId, 
                                taskId = result.TaskId,
                                success = result.Success,
                                result = result.Result,
                                error = result.Error
                            })
                        });
                    };
                    
                    orchestrator.OnPhaseCompleted += (phase) =>
                    {
                        writer.TryWrite(new AgentStreamEvent { Type = "phase_completed", Data = phase });
                    };
                    
                    // Execute the plan
                    var result = await orchestrator.ExecutePlanAsync(plan, ct, planPath, retryFailed, skipFailed);
                    
                    // Send completion summary
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine($"## Orchestration Complete\n");
                    sb.AppendLine($"**Duration:** {result.Duration.TotalMinutes:F1} minutes");
                    sb.AppendLine($"**Tasks:** {result.TaskResults.Count(r => r.Success)} succeeded, {result.TaskResults.Count(r => !r.Success)} failed\n");
                    
                    if (!string.IsNullOrEmpty(result.Error))
                    {
                        sb.AppendLine($"⚠️ **Error:** {result.Error}\n");
                    }
                    
                    writer.TryWrite(new AgentStreamEvent 
                    { 
                        Type = "orchestration_completed", 
                        Data = JsonSerializer.Serialize(new { success = result.TaskResults.All(r => r.Success) })
                    });
                    
                    writer.TryWrite(new AgentStreamEvent { Type = "command_result", Data = sb.ToString() });
                }
                catch (OperationCanceledException)
                {
                    writer.TryWrite(new AgentStreamEvent { Type = "cancelled", Data = "Orchestration cancelled" });
                }
                catch (Exception ex)
                {
                    writer.TryWrite(new AgentStreamEvent { Type = "error", Data = $"Orchestration failed: {ex.Message}" });
                }
                finally
                {
                    writer.Complete();
                }
            }, ct);
            
            // Stream events as they arrive
            await foreach (var evt in channel.Reader.ReadAllAsync(ct))
            {
                yield return evt;
            }
            
            await orchestrationTask;
        }

        private async Task ProcessCommandAsync(
            string sessionId,
            string command,
            string cmdName,
            WebAgentSession session,
            List<AgentStreamEvent> events,
            CancellationToken ct)
        {
            try
            {
                switch (cmdName)
                {
                    case "/help":
                        events.Add(new AgentStreamEvent { Type = "command_result", Data = GetHelpText() });
                        break;

                    case "/clear":
                        ClearSession(sessionId);
                        events.Add(new AgentStreamEvent { Type = "command_result", Data = "✓ Conversation cleared." });
                        break;

                    case "/config":
                        var config = GetConfig();
                        events.Add(new AgentStreamEvent { Type = "command_result", Data = FormatConfig(config) });
                        break;

                    case "/status":
                        var status = $"Session: {sessionId}\nMessages: {session.Messages.Count}\nProcessing: {session.IsProcessing}";
                        events.Add(new AgentStreamEvent { Type = "command_result", Data = status });
                        break;

                    case "/diff":
                        events.Add(new AgentStreamEvent { Type = "status", Data = "Running git diff..." });
                        var diffResult = await RunCommandAsync(() => CommandHandlers.HandleDiffCommandAsync(command, ct, null));
                        events.Add(new AgentStreamEvent { Type = "command_result", Data = diffResult });
                        break;

                    case "/test":
                        events.Add(new AgentStreamEvent { Type = "status", Data = "Running tests..." });
                        var testResult = await RunCommandAsync(() => CommandHandlers.HandleTestCommandAsync(command, ct, null));
                        events.Add(new AgentStreamEvent { Type = "command_result", Data = testResult });
                        break;

                    case "/run":
                        events.Add(new AgentStreamEvent { Type = "status", Data = "Running command..." });
                        var runResult = await RunCommandAsync(() => CommandHandlers.HandleRunCommandAsync(command, ct, null));
                        events.Add(new AgentStreamEvent { Type = "command_result", Data = runResult });
                        break;

                    case "/models":
                        var modelsInfo = GetModelsInfo();
                        events.Add(new AgentStreamEvent { Type = "command_result", Data = modelsInfo });
                        break;

                    case "/health":
                        events.Add(new AgentStreamEvent { Type = "status", Data = "Running health checks..." });
                        var healthResult = await RunHealthCheckAsync(ct);
                        events.Add(new AgentStreamEvent { Type = "command_result", Data = healthResult });
                        break;

                    case "/system":
                        var parts = command.Split(' ', 2);
                        if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[1]))
                        {
                            // Show current system prompt
                            var currentPrompt = session.Messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "(none)";
                            events.Add(new AgentStreamEvent { Type = "command_result", Data = $"**Current system prompt:**\n\n{currentPrompt}" });
                        }
                        else
                        {
                            // Set new system prompt
                            var newPrompt = parts[1].Trim();
                            var systemMsg = session.Messages.FirstOrDefault(m => m.Role == "system");
                            if (systemMsg != null)
                            {
                                systemMsg.Content = newPrompt;
                            }
                            else
                            {
                                session.Messages.Insert(0, new ChatMessage("system", newPrompt));
                            }
                            events.Add(new AgentStreamEvent { Type = "command_result", Data = $"✓ System prompt updated." });
                        }
                        break;

                    case "/stream":
                        var streamParts = command.Split(' ', 2);
                        if (streamParts.Length < 2)
                        {
                            events.Add(new AgentStreamEvent { Type = "command_result", Data = $"Streaming: {(AgentConfig.Config.Stream ? "on" : "off")}" });
                        }
                        else
                        {
                            var val = streamParts[1].Trim().ToLowerInvariant();
                            AgentConfig.Config.Stream = val == "on" || val == "true" || val == "1";
                            events.Add(new AgentStreamEvent { Type = "command_result", Data = $"✓ Streaming set to: {(AgentConfig.Config.Stream ? "on" : "off")}" });
                        }
                        break;

                    case "/plan":
                        var planParts = command.Split(' ', 2);
                        if (planParts.Length < 2 || string.IsNullOrWhiteSpace(planParts[1]))
                        {
                            events.Add(new AgentStreamEvent { Type = "command_result", Data = GetPlanHelpText() });
                        }
                        else
                        {
                            events.Add(new AgentStreamEvent { Type = "status", Data = "Analyzing task..." });
                            var planResult = await HandlePlanAsync(planParts[1].Trim(), ct);
                            events.Add(new AgentStreamEvent { Type = "command_result", Data = planResult });
                        }
                        break;

                    case "/orchestrate":
                        // Handled by ExecuteOrchestrationAsync - this shouldn't be reached
                        events.Add(new AgentStreamEvent { Type = "error", Data = "Orchestration routing error" });
                        break;

                    case "/browser":
                        await HandleBrowserCommandAsync(command, events, ct);
                        break;

                    default:
                        events.Add(new AgentStreamEvent { Type = "error", Data = $"Unknown command: {cmdName}\nType /help for available commands." });
                        break;
                }
            }
            catch (Exception ex)
            {
                events.Add(new AgentStreamEvent { Type = "error", Data = $"Command failed: {ex.Message}" });
            }
        }

        private static string GetPlanHelpText()
        {
            return @"**Task Planning**

Usage: `/plan <task description>`

Examples:
- `/plan Create a REST API for user management`
- `/plan Add unit tests for the Calculator class`
- `/plan Refactor the authentication module`

The agent will analyze the task and suggest a decomposition into subtasks.";
        }

        private async Task<string> HandlePlanAsync(string taskDescription, CancellationToken ct)
        {
            try
            {
                // Get codebase context from work directory
                string? codebaseContext = null;
                var workDir = AgentConfig.GetWorkDirectory();
                
                try
                {
                    var files = Directory.GetFiles(workDir, "*.cs", SearchOption.AllDirectories)
                        .Take(20)
                        .Select(f => Path.GetRelativePath(workDir, f))
                        .ToList();
                    
                    if (files.Any())
                    {
                        codebaseContext = $"Existing project files: {string.Join(", ", files.Take(10))}";
                    }
                    else
                    {
                        codebaseContext = "Work directory is empty - this will be a new project.";
                    }
                }
                catch { /* Ignore context errors */ }

                // Use the TaskDecomposer service
                var decomposer = new TaskDecomposer(_http);
                var plan = await decomposer.DecomposeAsync(taskDescription, codebaseContext, ct);
                
                if (plan == null || plan.SubTasks.Count == 0)
                {
                    return "Could not decompose the task. Try being more specific.";
                }

                // Save plan to work directory (so it shows in workspace file browser)
                var jsonPath = Path.Combine(workDir, "current-plan.json");
                var mdPath = Path.Combine(workDir, "current-plan.md");
                
                try
                {
                    // Ensure work directory exists
                    Directory.CreateDirectory(workDir);
                    plan.SaveToFile(jsonPath);
                    plan.SaveToMarkdown(mdPath);
                }
                catch (Exception saveEx)
                {
                    SessionLogger.Instance.LogError($"Could not save plan files: {saveEx.Message}");
                }

                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"**Task Plan: {plan.OriginalRequest}**\n");
                sb.AppendLine($"Summary: {plan.Summary}\n");
                sb.AppendLine($"Recommended Agents: {plan.RecommendedAgentCount} | Est. Time: {plan.TotalEstimatedMinutes} min\n");
                sb.AppendLine("**Subtasks:**\n");
                
                for (int i = 0; i < plan.SubTasks.Count; i++)
                {
                    var subtask = plan.SubTasks[i];
                    sb.AppendLine($"{i + 1}. **{subtask.Title}** ({subtask.Type}, ~{subtask.EstimatedMinutes}min)");
                    sb.AppendLine($"   {subtask.Description}");
                    if (subtask.Dependencies.Any())
                    {
                        sb.AppendLine($"   Dependencies: {string.Join(", ", subtask.Dependencies)}");
                    }
                    sb.AppendLine();
                }

                sb.AppendLine($"\n📁 Plan saved to: `{Path.GetFileName(jsonPath)}` and `{Path.GetFileName(mdPath)}`");
                sb.AppendLine("\n*Use `/orchestrate` to execute this plan with multiple agents.*");
                
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"Error planning task: {ex.Message}";
            }
        }

        private async Task HandleBrowserCommandAsync(string command, List<AgentStreamEvent> events, CancellationToken ct)
        {
            var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var subCommand = parts.Length > 1 ? parts[1].ToLowerInvariant() : "help";

            switch (subCommand)
            {
                case "install":
                    events.Add(new AgentStreamEvent { Type = "status", Data = "Installing Playwright browsers..." });
                    var installResult = await thuvu.Tools.BrowserToolImpl.InstallBrowsersAsync();
                    events.Add(new AgentStreamEvent { Type = "command_result", Data = installResult });
                    break;

                case "open":
                    if (parts.Length < 3)
                    {
                        events.Add(new AgentStreamEvent { Type = "error", Data = "Usage: /browser open <url>" });
                        return;
                    }
                    var url = parts[2];
                    events.Add(new AgentStreamEvent { Type = "status", Data = $"Navigating to {url}..." });
                    
                    var browseResult = await thuvu.Tools.BrowserToolImpl.BrowseUrlAsync(
                        JsonSerializer.Serialize(new { url, extract_text = true, screenshot = true }), ct);
                    
                    try
                    {
                        using var doc = JsonDocument.Parse(browseResult);
                        var root = doc.RootElement;
                        
                        if (root.TryGetProperty("error", out var err))
                        {
                            events.Add(new AgentStreamEvent { Type = "error", Data = $"Error: {err.GetString()}" });
                        }
                        else
                        {
                            var title = root.TryGetProperty("title", out var t) ? t.GetString() : "Untitled";
                            var pageUrl = root.TryGetProperty("url", out var u) ? u.GetString() : url;
                            
                            var sb = new System.Text.StringBuilder();
                            sb.AppendLine($"**{title}**");
                            sb.AppendLine($"*{pageUrl}*\n");
                            
                            // Include screenshot if available
                            if (root.TryGetProperty("screenshot_base64", out var screenshotProp))
                            {
                                var screenshot = screenshotProp.GetString();
                                events.Add(new AgentStreamEvent 
                                { 
                                    Type = "browser_screenshot", 
                                    Data = screenshot ?? ""
                                });
                            }
                            
                            if (root.TryGetProperty("text", out var text))
                            {
                                var content = text.GetString() ?? "";
                                if (content.Length > 3000)
                                    content = content.Substring(0, 3000) + "\n\n... [truncated]";
                                sb.AppendLine(content);
                            }
                            
                            events.Add(new AgentStreamEvent { Type = "command_result", Data = sb.ToString() });
                        }
                    }
                    catch
                    {
                        events.Add(new AgentStreamEvent { Type = "command_result", Data = browseResult });
                    }
                    break;

                case "screenshot":
                    events.Add(new AgentStreamEvent { Type = "status", Data = "Taking screenshot..." });
                    var ssResult = await thuvu.Tools.BrowserToolImpl.ScreenshotAsync("{}", ct);
                    
                    try
                    {
                        using var ssDoc = JsonDocument.Parse(ssResult);
                        var ssRoot = ssDoc.RootElement;
                        
                        if (ssRoot.TryGetProperty("screenshot_base64", out var ssData))
                        {
                            events.Add(new AgentStreamEvent 
                            { 
                                Type = "browser_screenshot", 
                                Data = ssData.GetString() ?? ""
                            });
                            events.Add(new AgentStreamEvent { Type = "command_result", Data = "Screenshot captured." });
                        }
                        else if (ssRoot.TryGetProperty("error", out var ssErr))
                        {
                            events.Add(new AgentStreamEvent { Type = "error", Data = ssErr.GetString() ?? "Unknown error" });
                        }
                    }
                    catch
                    {
                        events.Add(new AgentStreamEvent { Type = "command_result", Data = ssResult });
                    }
                    break;

                case "close":
                    await thuvu.Tools.BrowserToolImpl.CloseBrowserAsync();
                    events.Add(new AgentStreamEvent { Type = "command_result", Data = "✓ Browser closed." });
                    break;

                default:
                    events.Add(new AgentStreamEvent 
                    { 
                        Type = "command_result", 
                        Data = @"**Browser Commands**

| Command | Description |
|---------|-------------|
| `/browser install` | Install Playwright browsers (required first time) |
| `/browser open <url>` | Navigate to URL and show content with screenshot |
| `/browser screenshot` | Take screenshot of current page |
| `/browser close` | Close the browser |

The LLM can also use browser tools directly: `browser_navigate`, `browser_click`, `browser_type`, `browser_get_elements`, `browser_screenshot`, `browser_script`"
                    });
                    break;
            }
        }

        private async Task<string> HandleOrchestrateAsync(
            List<AgentStreamEvent> events, 
            int agentCount, 
            bool retryFailed, 
            bool skipFailed,
            CancellationToken ct)
        {
            try
            {
                var workDir = AgentConfig.GetWorkDirectory();
                var planPath = Path.Combine(workDir, "current-plan.json");
                
                if (!File.Exists(planPath))
                {
                    return "❌ No plan found. Use `/plan <task>` first to create a plan.";
                }
                
                // Load the plan
                var planJson = await File.ReadAllTextAsync(planPath, ct);
                var plan = JsonSerializer.Deserialize<TaskPlan>(planJson);
                
                if (plan == null || plan.SubTasks.Count == 0)
                {
                    return "❌ Invalid or empty plan. Use `/plan <task>` to create a new plan.";
                }
                
                // Send plan info to UI
                events.Add(new AgentStreamEvent 
                { 
                    Type = "orchestration_plan", 
                    Data = JsonSerializer.Serialize(new 
                    {
                        taskId = plan.TaskId,
                        taskName = plan.OriginalRequest,
                        subtasks = plan.SubTasks.Select(t => new 
                        {
                            id = t.Id,
                            title = t.Title,
                            description = t.Description,
                            status = t.Status.ToString()
                        }).ToList()
                    })
                });
                
                var config = new OrchestratorConfig 
                { 
                    MaxAgents = agentCount,
                    UseProcessIsolation = false // Use in-process for web
                };
                
                using var orchestrator = new TaskOrchestrator(_http, config, workDir);
                
                // Wire up events
                orchestrator.OnAgentStarted += (agentId, taskId) =>
                {
                    events.Add(new AgentStreamEvent 
                    { 
                        Type = "agent_started", 
                        Data = JsonSerializer.Serialize(new { agentId, taskId })
                    });
                };
                
                orchestrator.OnAgentOutput += (agentId, text) =>
                {
                    events.Add(new AgentStreamEvent 
                    { 
                        Type = "agent_output", 
                        Data = JsonSerializer.Serialize(new { agentId, text })
                    });
                };
                
                orchestrator.OnTaskCompleted += (agentId, result) =>
                {
                    events.Add(new AgentStreamEvent 
                    { 
                        Type = "agent_completed", 
                        Data = JsonSerializer.Serialize(new 
                        { 
                            agentId, 
                            taskId = result.TaskId,
                            success = result.Success,
                            result = result.Result,
                            error = result.Error
                        })
                    });
                };
                
                orchestrator.OnPhaseCompleted += (phase) =>
                {
                    events.Add(new AgentStreamEvent { Type = "phase_completed", Data = phase });
                };
                
                orchestrator.OnPlanCompleted += (completedPlan, success) =>
                {
                    events.Add(new AgentStreamEvent 
                    { 
                        Type = "orchestration_completed", 
                        Data = JsonSerializer.Serialize(new { success })
                    });
                };
                
                // Execute the plan
                var result = await orchestrator.ExecutePlanAsync(plan, ct, planPath, retryFailed, skipFailed);
                
                // Build summary
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"## Orchestration Complete\n");
                sb.AppendLine($"**Plan:** {plan.OriginalRequest}");
                sb.AppendLine($"**Duration:** {result.Duration.TotalMinutes:F1} minutes");
                sb.AppendLine($"**Tasks:** {result.TaskResults.Count(r => r.Success)} succeeded, {result.TaskResults.Count(r => !r.Success)} failed\n");
                
                if (!string.IsNullOrEmpty(result.Error))
                {
                    sb.AppendLine($"⚠️ **Error:** {result.Error}\n");
                }
                
                // List results
                sb.AppendLine("### Task Results\n");
                foreach (var taskResult in result.TaskResults)
                {
                    var icon = taskResult.Success ? "✅" : "❌";
                    sb.AppendLine($"{icon} **{taskResult.TaskId}** ({taskResult.Duration.TotalSeconds:F0}s)");
                    if (!string.IsNullOrEmpty(taskResult.Error))
                    {
                        sb.AppendLine($"   Error: {taskResult.Error}");
                    }
                }
                
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"❌ Orchestration failed: {ex.Message}";
            }
        }

        private static string GetHelpText()
        {
            return @"**Available Commands:**

| Command | Description |
|---------|-------------|
| `/help` | Show this help message |
| `/clear` | Clear conversation history |
| `/system [text]` | View or set system prompt |
| `/stream on\|off` | Toggle streaming mode |
| `/config` | Show current configuration |
| `/status` | Show session status |
| `/diff` | Show git diff |
| `/test` | Run dotnet tests |
| `/run <cmd>` | Run a shell command |
| `/models` | List available models |
| `/health` | Check service health |
| `/plan <task>` | Decompose a task into subtasks |
| `/orchestrate [opts]` | Run multi-agent orchestration |

**Orchestration Options:**
- `--agents N` - Number of agents (1-4, default: 2)
- `--retry` - Retry failed tasks
- `--skip` - Skip blocked tasks

*Tip: Use `@filename` to reference files in your messages.*";
        }

        private static string FormatConfig(object config)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(config, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            return $"**Current Configuration:**\n```json\n{json}\n```";
        }

        private static string GetModelsInfo()
        {
            var models = ModelRegistry.Instance.Models;
            var lines = new List<string> { "**Available Models:**\n" };
            foreach (var m in models.Where(m => m.Enabled))
            {
                var current = m.ModelId == AgentConfig.Config.Model ? " ← current" : "";
                lines.Add($"- **{m.DisplayName}** (`{m.ModelId}`){current}");
                lines.Add($"  Host: {m.HostUrl}, Local: {m.IsLocal}");
            }
            return string.Join("\n", lines);
        }

        private static async Task<string> RunCommandAsync(Func<Task> action)
        {
            var output = new System.Text.StringBuilder();
            try
            {
                await action();
                return output.Length > 0 ? output.ToString() : "✓ Command completed.";
            }
            catch (Exception ex)
            {
                return $"❌ Error: {ex.Message}";
            }
        }

        private static async Task<string> RunHealthCheckAsync(CancellationToken ct)
        {
            var results = new List<string>();
            
            // Check LLM
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var response = await http.GetAsync($"{AgentConfig.Config.HostUrl}/v1/models", ct);
                results.Add(response.IsSuccessStatusCode ? "✅ LLM API: Connected" : $"❌ LLM API: {response.StatusCode}");
            }
            catch (Exception ex)
            {
                results.Add($"❌ LLM API: {ex.Message}");
            }

            // Check RAG
            if (RagConfig.Instance.Enabled)
            {
                try
                {
                    // Simple check - just report config
                    results.Add($"✅ RAG: Enabled (embedding: {RagConfig.Instance.EmbeddingModel})");
                }
                catch
                {
                    results.Add("❌ RAG: Configuration error");
                }
            }
            else
            {
                results.Add("⚪ RAG: Disabled");
            }

            // Check MCP
            results.Add(McpConfig.Instance.Enabled ? "✅ MCP: Enabled" : "⚪ MCP: Disabled");

            return "**Health Check Results:**\n\n" + string.Join("\n", results);
        }

        /// <summary>
        /// Delete a session
        /// </summary>
        public bool DeleteSession(string sessionId)
        {
            return _sessions.TryRemove(sessionId, out _);
        }

        /// <summary>
        /// Get all active sessions
        /// </summary>
        public IEnumerable<WebAgentSession> GetAllSessions()
        {
            return _sessions.Values.OrderByDescending(s => s.LastActivityAt);
        }

        /// <summary>
        /// Process @file references in a message, expanding file contents inline
        /// </summary>
        private async Task<string> ProcessFileReferencesAsync(string message)
        {
            if (string.IsNullOrEmpty(message) || !message.Contains('@'))
                return message;

            var workDir = AgentConfig.GetWorkDirectory();
            var result = message;
            var fileContents = new List<string>();
            
            // Find all @file references using regex
            // Match @followed by path characters until whitespace or end
            var regex = new System.Text.RegularExpressions.Regex(@"@([\w\-./\\]+\.[\w]+)");
            var matches = regex.Matches(message);
            
            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                var relativePath = match.Groups[1].Value;
                var fullPath = Path.GetFullPath(Path.Combine(workDir, relativePath));
                
                // Security check - must be within work directory
                if (!fullPath.StartsWith(workDir, StringComparison.OrdinalIgnoreCase))
                    continue;
                    
                if (File.Exists(fullPath))
                {
                    try
                    {
                        var content = await File.ReadAllTextAsync(fullPath);
                        // Limit file size to avoid token overflow
                        if (content.Length > 50000)
                        {
                            content = content[..50000] + "\n... (truncated)";
                        }
                        
                        fileContents.Add($"**File: {relativePath}**\n```\n{content}\n```");
                        
                        // Replace @file with just the filename for cleaner display
                        result = result.Replace(match.Value, $"`{relativePath}`");
                    }
                    catch
                    {
                        // File read error - leave reference as-is
                    }
                }
            }
            
            // Append file contents at the end of the message
            if (fileContents.Count > 0)
            {
                result += "\n\n---\n**Referenced Files:**\n\n" + string.Join("\n\n", fileContents);
            }
            
            return result;
        }

        /// <summary>
        /// Get current configuration info
        /// </summary>
        public object GetConfig()
        {
            return new
            {
                model = AgentConfig.Config.Model,
                hostUrl = AgentConfig.Config.HostUrl,
                workDirectory = AgentConfig.GetWorkDirectory(),
                streaming = AgentConfig.Config.Stream,
                mcpEnabled = McpConfig.Instance.Enabled,
                ragEnabled = RagConfig.Instance.Enabled
            };
        }

        /// <summary>
        /// Get file suggestions for autocomplete
        /// </summary>
        public List<string> GetFileSuggestions(string prefix)
        {
            var results = new List<string>();
            var workDir = AgentConfig.GetWorkDirectory();
            
            try
            {
                string searchDir;
                string searchPattern;
                
                // Handle empty prefix - list root work directory
                if (string.IsNullOrEmpty(prefix))
                {
                    searchDir = workDir;
                    searchPattern = "*";
                }
                else
                {
                    // Normalize the prefix
                    prefix = prefix.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                    
                    if (prefix.StartsWith("." + Path.DirectorySeparatorChar) || prefix.StartsWith(".." + Path.DirectorySeparatorChar))
                    {
                        // Relative path
                        var fullPath = Path.GetFullPath(Path.Combine(workDir, prefix));
                        if (Directory.Exists(fullPath))
                        {
                            searchDir = fullPath;
                            searchPattern = "*";
                        }
                        else
                        {
                            searchDir = Path.GetDirectoryName(fullPath) ?? workDir;
                            searchPattern = Path.GetFileName(fullPath) + "*";
                        }
                    }
                    else if (Path.IsPathRooted(prefix))
                    {
                        // Absolute path
                        if (Directory.Exists(prefix))
                        {
                            searchDir = prefix;
                            searchPattern = "*";
                        }
                        else
                        {
                            searchDir = Path.GetDirectoryName(prefix) ?? workDir;
                            searchPattern = Path.GetFileName(prefix) + "*";
                        }
                    }
                    else
                    {
                        // Just a filename or partial path - search recursively
                        searchDir = workDir;
                        searchPattern = "*" + prefix + "*";
                    }
                }

                if (!Directory.Exists(searchDir))
                    return results;

                // Get directories first
                foreach (var dir in Directory.GetDirectories(searchDir, searchPattern).Take(5))
                {
                    var relativePath = Path.GetRelativePath(workDir, dir);
                    results.Add(relativePath + Path.DirectorySeparatorChar);
                }

                // Then files
                foreach (var file in Directory.GetFiles(searchDir, searchPattern).Take(10 - results.Count))
                {
                    var relativePath = Path.GetRelativePath(workDir, file);
                    results.Add(relativePath);
                }
            }
            catch
            {
                // Ignore errors - just return empty list
            }

            return results.Take(10).ToList();
        }

        /// <summary>
        /// Get directory contents for file tree
        /// </summary>
        public List<Hubs.FileTreeItem> GetDirectoryContents(string relativePath)
        {
            var results = new List<Hubs.FileTreeItem>();
            var workDir = AgentConfig.GetWorkDirectory();
            
            try
            {
                var fullPath = Path.GetFullPath(Path.Combine(workDir, relativePath));
                
                // Security: ensure path is within work directory
                if (!fullPath.StartsWith(workDir, StringComparison.OrdinalIgnoreCase))
                {
                    return results;
                }

                if (!Directory.Exists(fullPath))
                {
                    return results;
                }

                // Get directories first
                foreach (var dir in Directory.GetDirectories(fullPath))
                {
                    var dirInfo = new DirectoryInfo(dir);
                    // Skip hidden and common ignored directories
                    if (dirInfo.Name.StartsWith(".") || 
                        dirInfo.Name == "node_modules" || 
                        dirInfo.Name == "bin" || 
                        dirInfo.Name == "obj" ||
                        dirInfo.Name == "__pycache__")
                        continue;

                    results.Add(new Hubs.FileTreeItem
                    {
                        Name = dirInfo.Name,
                        Path = Path.GetRelativePath(workDir, dir),
                        IsDirectory = true,
                        IsLoaded = false
                    });
                }

                // Then files
                foreach (var file in Directory.GetFiles(fullPath))
                {
                    var fileInfo = new FileInfo(file);
                    // Skip hidden files
                    if (fileInfo.Name.StartsWith(".") && fileInfo.Name != ".gitignore")
                        continue;

                    results.Add(new Hubs.FileTreeItem
                    {
                        Name = fileInfo.Name,
                        Path = Path.GetRelativePath(workDir, file),
                        IsDirectory = false
                    });
                }
            }
            catch
            {
                // Ignore errors
            }

            return results
                .OrderByDescending(i => i.IsDirectory)
                .ThenBy(i => i.Name)
                .ToList();
        }

        /// <summary>
        /// Read file contents
        /// </summary>
        public Hubs.FileContentResult ReadFile(string relativePath)
        {
            var workDir = AgentConfig.GetWorkDirectory();
            
            try
            {
                var fullPath = Path.GetFullPath(Path.Combine(workDir, relativePath));
                
                // Security: ensure path is within work directory
                if (!fullPath.StartsWith(workDir, StringComparison.OrdinalIgnoreCase))
                {
                    return new Hubs.FileContentResult { Success = false, Error = "Access denied" };
                }

                if (!File.Exists(fullPath))
                {
                    return new Hubs.FileContentResult { Success = false, Error = "File not found" };
                }

                var fileInfo = new FileInfo(fullPath);
                
                // Check file size (limit to 1MB for text display)
                if (fileInfo.Length > 1024 * 1024)
                {
                    return new Hubs.FileContentResult 
                    { 
                        Success = false, 
                        Error = $"File too large ({fileInfo.Length / 1024}KB). Maximum 1MB."
                    };
                }

                // Check if binary
                var extension = fileInfo.Extension.ToLowerInvariant();
                var binaryExtensions = new[] { ".exe", ".dll", ".pdb", ".png", ".jpg", ".jpeg", ".gif", ".ico", ".zip", ".tar", ".gz", ".7z", ".rar", ".pdf", ".doc", ".docx", ".xls", ".xlsx" };
                
                if (binaryExtensions.Contains(extension))
                {
                    return new Hubs.FileContentResult { Success = true, IsBinary = true, Content = "" };
                }

                var content = File.ReadAllText(fullPath);
                return new Hubs.FileContentResult { Success = true, Content = content };
            }
            catch (Exception ex)
            {
                return new Hubs.FileContentResult { Success = false, Error = ex.Message };
            }
        }

        /// <summary>
        /// Truncate tool result by truncating individual JSON field values rather than the whole string.
        /// This preserves the JSON structure while limiting very long field values.
        /// </summary>
        private static string TruncateResult(string result, int maxFieldLength = 500)
        {
            if (string.IsNullOrWhiteSpace(result))
                return result;
            
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(result);
                var truncated = TruncateJsonElement(doc.RootElement, maxFieldLength);
                return JsonSerializer.Serialize(truncated, new System.Text.Json.JsonSerializerOptions 
                { 
                    WriteIndented = true 
                });
            }
            catch
            {
                // Not valid JSON - truncate plain text if too long
                if (result.Length > 5000)
                {
                    return result[..5000] + $"\n... (truncated, {result.Length - 5000} more chars)";
                }
                return result;
            }
        }

        /// <summary>
        /// Truncate result for database storage (larger limit than UI display)
        /// </summary>
        private static string TruncateForDb(string result, int maxLength = 50000)
        {
            if (string.IsNullOrWhiteSpace(result) || result.Length <= maxLength)
                return result;
            
            return result[..maxLength] + $"\n... (truncated, {result.Length - maxLength} more chars)";
        }
        
        /// <summary>
        /// Recursively truncate long string values in a JSON element
        /// </summary>
        private static object? TruncateJsonElement(System.Text.Json.JsonElement element, int maxFieldLength)
        {
            switch (element.ValueKind)
            {
                case System.Text.Json.JsonValueKind.Object:
                    var obj = new Dictionary<string, object?>();
                    foreach (var prop in element.EnumerateObject())
                    {
                        obj[prop.Name] = TruncateJsonElement(prop.Value, maxFieldLength);
                    }
                    return obj;
                    
                case System.Text.Json.JsonValueKind.Array:
                    var arr = new List<object?>();
                    foreach (var item in element.EnumerateArray())
                    {
                        arr.Add(TruncateJsonElement(item, maxFieldLength));
                    }
                    return arr;
                    
                case System.Text.Json.JsonValueKind.String:
                    var str = element.GetString() ?? "";
                    if (str.Length > maxFieldLength)
                    {
                        return str[..maxFieldLength] + $"... ({str.Length - maxFieldLength} more chars)";
                    }
                    return str;
                    
                case System.Text.Json.JsonValueKind.Number:
                    if (element.TryGetInt64(out var longVal)) return longVal;
                    if (element.TryGetDouble(out var doubleVal)) return doubleVal;
                    return element.GetRawText();
                    
                case System.Text.Json.JsonValueKind.True:
                    return true;
                    
                case System.Text.Json.JsonValueKind.False:
                    return false;
                    
                case System.Text.Json.JsonValueKind.Null:
                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// Event streamed from agent to client
    /// </summary>
    public class AgentStreamEvent
    {
        [System.Text.Json.Serialization.JsonPropertyName("type")]
        public string Type { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("data")]
        public string Data { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("toolName")]
        public string? ToolName { get; set; }
    }

    /// <summary>
    /// DTO for chat message display in UI
    /// </summary>
    public class ChatMessageDisplayDto
    {
        [System.Text.Json.Serialization.JsonPropertyName("role")]
        public string Role { get; set; } = "";
        
        [System.Text.Json.Serialization.JsonPropertyName("content")]
        public string Content { get; set; } = "";
        
        [System.Text.Json.Serialization.JsonPropertyName("toolName")]
        public string? ToolName { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("toolArgs")]
        public string? ToolArgs { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("agentDepth")]
        public int AgentDepth { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("agentRole")]
        public string? AgentRole { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("isSubAgentDelegation")]
        public bool IsSubAgentDelegation { get; set; }
    }
}
