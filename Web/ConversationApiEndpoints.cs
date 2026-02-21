using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using thuvu.Models;

namespace thuvu.Web
{
    /// <summary>
    /// Conversation-based API endpoints for the client/server architecture.
    /// Sits alongside the existing job-based API for backward compatibility.
    /// </summary>
    public static class ConversationApiEndpoints
    {
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        /// <summary>
        /// Callback to process a conversation message through the agent loop.
        /// Set by Program.cs after the agent is initialized.
        /// Parameters: conversationId, prompt, eventEmitter, cancellationToken, model?, systemPrompt?
        /// Returns: final assistant response text
        /// </summary>
        public static Func<string, string, List<ConversationMessage>, Action<AgentStreamEvent>, CancellationToken, string?, string?, string?, Task<string>>?
            ProcessMessageCallback { get; set; }

        /// <summary>
        /// Callback to execute a slash command.
        /// Parameters: conversationId, command, cancellationToken
        /// Returns: command output text
        /// </summary>
        public static Func<string, string, CancellationToken, Task<CommandResult>>?
            ProcessCommandCallback { get; set; }

        public static void MapConversationApi(this IEndpointRouteBuilder app)
        {
            var api = app.MapGroup("/api/conversations");

            // Apply same auth as job API
            if (!string.IsNullOrEmpty(AgentApiConfig.Instance.BearerToken))
            {
                api.AddEndpointFilter(AuthFilter);
            }

            api.MapPost("/", CreateConversation);
            api.MapGet("/", ListConversations);
            api.MapGet("/{id}", GetConversation);
            api.MapDelete("/{id}", DeleteConversation);
            api.MapPost("/{id}/messages", SendMessage);
            api.MapGet("/{id}/messages", GetMessages);
            api.MapPost("/{id}/cancel", CancelRequest);
            api.MapPost("/{id}/command", SendCommand);

            // Permission prompt relay
            app.MapPost("/api/permissions/{id}", RespondToPermission);

            // Server info endpoints
            app.MapGet("/api/config", GetServerConfig);
            app.MapGet("/api/health", GetHealth);
        }

        private static async ValueTask<object?> AuthFilter(
            EndpointFilterInvocationContext context,
            EndpointFilterDelegate next)
        {
            var expectedToken = AgentApiConfig.Instance.BearerToken;
            if (string.IsNullOrEmpty(expectedToken))
                return await next(context);

            var authHeader = context.HttpContext.Request.Headers.Authorization.ToString();
            if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return Results.Json(new { error = "Missing or invalid Authorization header" }, statusCode: 401);

            var token = authHeader["Bearer ".Length..].Trim();
            if (token != expectedToken)
                return Results.Json(new { error = "Invalid token" }, statusCode: 403);

            return await next(context);
        }

        /// <summary>
        /// POST /api/conversations - Create a new conversation
        /// </summary>
        private static async Task<IResult> CreateConversation(HttpRequest request)
        {
            try
            {
                var body = request.HasJsonContentType()
                    ? await JsonSerializer.DeserializeAsync<CreateConversationRequest>(request.Body, _jsonOptions)
                    : null;

                var conv = ConversationService.Instance.CreateConversation(
                    model: body?.Model,
                    systemPrompt: body?.SystemPrompt,
                    workDirectory: body?.WorkDirectory);

                return Results.Json(new
                {
                    id = conv.Id,
                    status = conv.Status.ToString().ToLower(),
                    createdAt = conv.CreatedAt,
                    model = conv.Model,
                    workDirectory = conv.WorkDirectory
                }, statusCode: 201);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 409);
            }
        }

        /// <summary>
        /// GET /api/conversations - List all conversations
        /// </summary>
        private static IResult ListConversations()
        {
            var convs = ConversationService.Instance.GetAllConversations();
            return Results.Json(new
            {
                count = convs.Count,
                conversations = convs.ConvertAll(c => new
                {
                    id = c.Id,
                    status = c.Status.ToString().ToLower(),
                    createdAt = c.CreatedAt,
                    lastActivityAt = c.LastActivityAt,
                    model = c.Model,
                    messageCount = c.Messages.Count
                })
            });
        }

        /// <summary>
        /// GET /api/conversations/{id} - Get conversation details
        /// </summary>
        private static IResult GetConversation(string id)
        {
            var conv = ConversationService.Instance.GetConversation(id);
            if (conv == null)
                return Results.Json(new { error = "Conversation not found" }, statusCode: 404);

            return Results.Json(new
            {
                id = conv.Id,
                status = conv.Status.ToString().ToLower(),
                createdAt = conv.CreatedAt,
                lastActivityAt = conv.LastActivityAt,
                model = conv.Model,
                systemPrompt = conv.SystemPrompt,
                workDirectory = conv.WorkDirectory,
                messageCount = conv.Messages.Count
            });
        }

        /// <summary>
        /// DELETE /api/conversations/{id} - End and remove a conversation
        /// </summary>
        private static IResult DeleteConversation(string id)
        {
            if (ConversationService.Instance.RemoveConversation(id))
                return Results.Json(new { success = true });
            return Results.Json(new { error = "Conversation not found" }, statusCode: 404);
        }

        /// <summary>
        /// POST /api/conversations/{id}/messages - Send a message, returns SSE stream
        /// Body: { "content": "...", "images": [{ "data": "base64...", "mimeType": "image/png" }] }
        /// </summary>
        private static async Task SendMessage(string id, HttpContext ctx, CancellationToken ct)
        {
            var conv = ConversationService.Instance.GetConversation(id);
            if (conv == null)
            {
                ctx.Response.StatusCode = 404;
                await ctx.Response.WriteAsJsonAsync(new { error = "Conversation not found" }, ct);
                return;
            }

            if (conv.Status == ConversationStatus.Processing)
            {
                ctx.Response.StatusCode = 409;
                await ctx.Response.WriteAsJsonAsync(new { error = "Conversation is already processing a message" }, ct);
                return;
            }

            SendMessageRequest? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<SendMessageRequest>(ctx.Request.Body, _jsonOptions, ct);
            }
            catch
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsJsonAsync(new { error = "Invalid request body" }, ct);
                return;
            }

            if (body == null || string.IsNullOrWhiteSpace(body.Content))
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsJsonAsync(new { error = "Missing content" }, ct);
                return;
            }

            if (ProcessMessageCallback == null)
            {
                ctx.Response.StatusCode = 503;
                await ctx.Response.WriteAsJsonAsync(new { error = "Agent not initialized" }, ct);
                return;
            }

            // Add user message to history
            conv.AddMessage("user", body.Content);
            conv.Status = ConversationStatus.Processing;

            // Set up SSE streaming
            var reader = conv.CreateEventChannel();
            var processingCt = conv.GetProcessingToken();
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, processingCt);

            ctx.Response.Headers["Content-Type"] = "text/event-stream";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            ctx.Response.Headers["Connection"] = "keep-alive";

            // Process in background, stream events to client
            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await ProcessMessageCallback(
                        conv.Id,
                        body.Content,
                        conv.Messages,
                        evt => conv.EmitEvent(evt),
                        linkedCts.Token,
                        conv.Model,
                        conv.SystemPrompt,
                        conv.WorkDirectory);

                    conv.AddMessage("assistant", result);
                    conv.EmitEvent(AgentStreamEvent.Complete(result));
                }
                catch (OperationCanceledException)
                {
                    conv.EmitEvent(AgentStreamEvent.Error("Request cancelled"));
                }
                catch (Exception ex)
                {
                    conv.EmitEvent(AgentStreamEvent.Error(ex.Message));
                }
                finally
                {
                    conv.Status = ConversationStatus.Idle;
                    conv.CompleteEventChannel();
                    linkedCts.Dispose();
                }
            }, CancellationToken.None);

            // Stream SSE events to the HTTP response
            try
            {
                if (reader != null)
                {
                    await foreach (var evt in reader.ReadAllAsync(ct))
                    {
                        await ctx.Response.WriteAsync($"event: {evt.Type}\ndata: {evt.Data}\n\n", ct);
                        await ctx.Response.Body.FlushAsync(ct);
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        /// <summary>
        /// GET /api/conversations/{id}/messages - Get conversation message history
        /// </summary>
        private static IResult GetMessages(string id, int? limit)
        {
            var conv = ConversationService.Instance.GetConversation(id);
            if (conv == null)
                return Results.Json(new { error = "Conversation not found" }, statusCode: 404);

            var messages = limit.HasValue
                ? conv.Messages.TakeLast(limit.Value).ToList()
                : conv.Messages;

            return Results.Json(new
            {
                conversationId = id,
                count = messages.Count,
                messages = messages.Select(m => new
                {
                    role = m.Role,
                    content = m.Content,
                    timestamp = m.Timestamp,
                    toolCalls = m.ToolCalls?.Select(tc => new
                    {
                        name = tc.Name,
                        args = tc.Args,
                        result = tc.Result,
                        elapsedSeconds = tc.ElapsedSeconds
                    })
                })
            });
        }

        /// <summary>
        /// POST /api/conversations/{id}/cancel - Cancel the current request
        /// </summary>
        private static IResult CancelRequest(string id)
        {
            var conv = ConversationService.Instance.GetConversation(id);
            if (conv == null)
                return Results.Json(new { error = "Conversation not found" }, statusCode: 404);

            if (conv.Status != ConversationStatus.Processing)
                return Results.Json(new { error = "No active request to cancel" }, statusCode: 409);

            conv.CancelProcessing();
            return Results.Json(new { success = true, message = "Cancellation requested" });
        }

        /// <summary>
        /// POST /api/conversations/{id}/command - Execute a slash command
        /// Body: { "command": "/diff --staged" }
        /// </summary>
        private static async Task<IResult> SendCommand(string id, HttpRequest request, CancellationToken ct)
        {
            var conv = ConversationService.Instance.GetConversation(id);
            if (conv == null)
                return Results.Json(new { error = "Conversation not found" }, statusCode: 404);

            var body = await JsonSerializer.DeserializeAsync<SendCommandRequest>(request.Body, _jsonOptions, ct);
            if (body == null || string.IsNullOrWhiteSpace(body.Command))
                return Results.Json(new { error = "Missing command" }, statusCode: 400);

            if (ProcessCommandCallback == null)
                return Results.Json(new { error = "Command processor not initialized" }, statusCode: 503);

            try
            {
                var result = await ProcessCommandCallback(id, body.Command, ct);
                return Results.Json(new
                {
                    success = result.Success,
                    output = result.Output,
                    error = result.Error
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, error = ex.Message }, statusCode: 500);
            }
        }

        /// <summary>
        /// POST /api/permissions/{id} - Respond to a permission prompt
        /// Body: { "approved": true }
        /// </summary>
        private static async Task<IResult> RespondToPermission(string id, HttpRequest request, CancellationToken ct)
        {
            var body = await JsonSerializer.DeserializeAsync<PermissionResponse>(request.Body, _jsonOptions, ct);
            if (body == null)
                return Results.Json(new { error = "Invalid request" }, statusCode: 400);

            // Search all conversations for this permission request
            foreach (var conv in ConversationService.Instance.GetAllConversations())
            {
                if (conv.RespondToPermission(id, body.Approved))
                    return Results.Json(new { success = true });
            }

            return Results.Json(new { error = "Permission request not found or expired" }, statusCode: 404);
        }

        /// <summary>
        /// GET /api/config - Get server configuration (safe subset)
        /// </summary>
        private static IResult GetServerConfig()
        {
            var config = AgentConfig.Config;
            var apiConfig = AgentApiConfig.Instance;

            return Results.Json(new
            {
                agent = new
                {
                    name = apiConfig.AgentName,
                    description = apiConfig.AgentDescription,
                    version = "1.0.0"
                },
                model = new
                {
                    current = config?.Model,
                    hostUrl = config?.HostUrl,
                    stream = config?.Stream,
                    maxContextLength = config?.MaxContextLength
                },
                capabilities = new
                {
                    rag = RagConfig.Instance?.Enabled ?? false,
                    mcp = McpConfig.Instance?.Enabled ?? false,
                    lsp = LspConfig.Config?.Enabled ?? false,
                    browser = true,
                    vision = !string.IsNullOrEmpty(ModelRegistry.Instance?.VisionModelId)
                }
            });
        }

        /// <summary>
        /// GET /api/health - Service health check
        /// </summary>
        private static IResult GetHealth()
        {
            return Results.Json(new
            {
                status = "healthy",
                timestamp = DateTime.UtcNow,
                conversations = ConversationService.Instance.GetAllConversations().Count,
                uptime = (DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds
            });
        }

        // Request/Response models
        private class CreateConversationRequest
        {
            public string? Model { get; set; }
            public string? SystemPrompt { get; set; }
            public string? WorkDirectory { get; set; }
        }

        private class SendMessageRequest
        {
            public string Content { get; set; } = "";
            public List<ImageAttachment>? Images { get; set; }
        }

        private class ImageAttachment
        {
            public string Data { get; set; } = ""; // base64
            public string MimeType { get; set; } = "image/png";
        }

        private class SendCommandRequest
        {
            public string Command { get; set; } = "";
        }

        private class PermissionResponse
        {
            public bool Approved { get; set; }
        }
    }

    /// <summary>
    /// Result from executing a slash command via the API.
    /// </summary>
    public class CommandResult
    {
        public bool Success { get; set; }
        public string Output { get; set; } = "";
        public string? Error { get; set; }

        public static CommandResult Ok(string output) => new() { Success = true, Output = output };
        public static CommandResult Fail(string error) => new() { Success = false, Error = error };
    }
}
