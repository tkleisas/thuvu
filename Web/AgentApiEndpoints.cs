using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using thuvu.Models;

namespace thuvu.Web
{
    /// <summary>
    /// Minimal API endpoints for agent-to-agent communication.
    /// </summary>
    public static class AgentApiEndpoints
    {
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        /// <summary>
        /// Map all agent API endpoints.
        /// </summary>
        public static void MapAgentApi(this IEndpointRouteBuilder app)
        {
            var api = app.MapGroup("/api");

            // Apply authentication if configured
            if (!string.IsNullOrEmpty(AgentApiConfig.Instance.BearerToken))
            {
                api.AddEndpointFilter(AuthFilter);
            }

            // Job endpoints
            api.MapPost("/jobs", SubmitJob);
            api.MapGet("/jobs/current", GetCurrentJob);
            api.MapGet("/jobs/{id}/stream", StreamJob);
            api.MapGet("/jobs/{id}", GetJob);
            api.MapDelete("/jobs/{id}", CancelJob);
            api.MapGet("/jobs", GetRecentJobs);

            // Agent info endpoints
            api.MapGet("/agent/info", GetAgentInfo);
        }

        private static async ValueTask<object?> AuthFilter(
            EndpointFilterInvocationContext context,
            EndpointFilterDelegate next)
        {
            var expectedToken = AgentApiConfig.Instance.BearerToken;
            if (string.IsNullOrEmpty(expectedToken))
            {
                return await next(context);
            }

            var authHeader = context.HttpContext.Request.Headers.Authorization.ToString();
            if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Json(new { error = "Missing or invalid Authorization header" }, statusCode: 401);
            }

            var token = authHeader["Bearer ".Length..].Trim();
            if (token != expectedToken)
            {
                return Results.Json(new { error = "Invalid token" }, statusCode: 403);
            }

            return await next(context);
        }

        /// <summary>
        /// POST /api/jobs - Submit a new job
        /// </summary>
        private static async Task<IResult> SubmitJob(HttpContext ctx, CancellationToken ct)
        {
            try
            {
                var body = await JsonSerializer.DeserializeAsync<SubmitJobRequest>(ctx.Request.Body, _jsonOptions, ct);
                if (body == null || string.IsNullOrWhiteSpace(body.Prompt))
                {
                    return Results.Json(new { success = false, error = "Missing prompt" }, statusCode: 400);
                }

                var jobService = AgentJobService.Instance;

                if (jobService.IsBusy)
                {
                    var currentJob = jobService.CurrentJob;
                    return Results.Json(new
                    {
                        success = false,
                        error = "Agent is busy",
                        currentJobId = currentJob?.Id,
                        currentJobStatus = currentJob?.Status.ToString().ToLower()
                    }, statusCode: 409);
                }

                var job = await jobService.SubmitJobAsync(body.Prompt, ct,
                    modelOverride: body.Model, systemPromptOverride: body.SystemPrompt);
                if (job == null)
                {
                    return Results.Json(new { success = false, error = "Failed to submit job" }, statusCode: 500);
                }

                // Signal the agent to process this job (done via event/callback in actual implementation)
                _ = Task.Run(() => AgentJobProcessor.ProcessJobAsync(job.Id, ct), ct);

                return Results.Json(new
                {
                    success = true,
                    jobId = job.Id,
                    status = job.Status.ToString().ToLower(),
                    submittedAt = job.SubmittedAt
                });
            }
            catch (Exception ex)
            {
                AgentLogger.LogError(ex, "Failed to submit job");
                return Results.Json(new { success = false, error = ex.Message }, statusCode: 500);
            }
        }

        /// <summary>
        /// GET /api/jobs/current - Get current job status and journal
        /// </summary>
        private static IResult GetCurrentJob()
        {
            var job = AgentJobService.Instance.CurrentJob;
            if (job == null)
            {
                return Results.Json(new
                {
                    success = true,
                    status = "idle",
                    message = "No active job"
                });
            }

            return Results.Json(new
            {
                success = true,
                job = FormatJob(job)
            });
        }

        /// <summary>
        /// GET /api/jobs/{id} - Get specific job
        /// </summary>
        private static async Task<IResult> GetJob(string id, CancellationToken ct)
        {
            var job = await AgentJobService.Instance.GetJobAsync(id, ct);
            if (job == null)
            {
                return Results.Json(new { success = false, error = "Job not found" }, statusCode: 404);
            }

            return Results.Json(new
            {
                success = true,
                job = FormatJob(job)
            });
        }

        /// <summary>
        /// GET /api/jobs/{id}/stream - SSE stream of job events
        /// </summary>
        private static async Task StreamJob(string id, HttpContext ctx, CancellationToken ct)
        {
            var job = await AgentJobService.Instance.GetJobAsync(id, ct);
            if (job == null)
            {
                ctx.Response.StatusCode = 404;
                await ctx.Response.WriteAsJsonAsync(new { success = false, error = "Job not found" }, ct);
                return;
            }

            // If job already completed, send single complete/error event and close
            if (job.Status == JobStatus.Completed || job.Status == JobStatus.Failed || job.Status == JobStatus.Cancelled)
            {
                ctx.Response.Headers["Content-Type"] = "text/event-stream";
                ctx.Response.Headers["Cache-Control"] = "no-cache";
                ctx.Response.Headers["Connection"] = "keep-alive";

                if (job.Status == JobStatus.Completed)
                    await WriteSseEvent(ctx.Response, "complete", JsonSerializer.Serialize(new { response = job.Result ?? "" }));
                else if (job.Status == JobStatus.Failed)
                    await WriteSseEvent(ctx.Response, "error", JsonSerializer.Serialize(new { message = job.Error ?? "Unknown error" }));
                else
                    await WriteSseEvent(ctx.Response, "error", JsonSerializer.Serialize(new { message = "Job was cancelled" }));
                return;
            }

            // Get event channel reader for active job
            var reader = AgentJobService.Instance.GetEventReader();
            if (reader == null)
            {
                ctx.Response.StatusCode = 409;
                await ctx.Response.WriteAsJsonAsync(new { success = false, error = "No active event stream" }, ct);
                return;
            }

            ctx.Response.Headers["Content-Type"] = "text/event-stream";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            ctx.Response.Headers["Connection"] = "keep-alive";

            try
            {
                await foreach (var evt in reader.ReadAllAsync(ct))
                {
                    await WriteSseEvent(ctx.Response, evt.Type, evt.Data);
                }
            }
            catch (OperationCanceledException) { }
            catch (ChannelClosedException) { }
        }

        private static async Task WriteSseEvent(HttpResponse response, string eventType, string data)
        {
            await response.WriteAsync($"event: {eventType}\ndata: {data}\n\n");
            await response.Body.FlushAsync();
        }

        /// <summary>
        /// DELETE /api/jobs/{id} - Cancel a job
        /// </summary>
        private static async Task<IResult> CancelJob(string id, CancellationToken ct)
        {
            var cancelled = await AgentJobService.Instance.CancelJobByIdAsync(id, ct);
            if (!cancelled)
            {
                return Results.Json(new { success = false, error = "Job not found or not cancellable" }, statusCode: 404);
            }

            return Results.Json(new { success = true, message = "Job cancelled" });
        }

        /// <summary>
        /// GET /api/jobs - Get recent jobs
        /// </summary>
        private static async Task<IResult> GetRecentJobs(int? limit, CancellationToken ct)
        {
            var jobs = await AgentJobService.Instance.GetRecentJobsAsync(limit ?? 50, ct);
            return Results.Json(new
            {
                success = true,
                count = jobs.Count,
                jobs = jobs.ConvertAll(FormatJobSummary)
            });
        }

        /// <summary>
        /// GET /api/agent/info - Get agent information
        /// </summary>
        private static IResult GetAgentInfo()
        {
            var config = AgentApiConfig.Instance;
            var job = AgentJobService.Instance.CurrentJob;

            return Results.Json(new
            {
                name = config.AgentName,
                description = config.AgentDescription,
                status = job == null ? "idle" : job.Status.ToString().ToLower(),
                currentJobId = job?.Id,
                model = AgentConfig.Config?.Model,
                version = "1.0.0"
            });
        }

        private static object FormatJob(AgentJob job)
        {
            return new
            {
                id = job.Id,
                status = job.Status.ToString().ToLower(),
                prompt = job.Prompt,
                result = job.Result,
                error = job.Error,
                submittedAt = job.SubmittedAt,
                startedAt = job.StartedAt,
                completedAt = job.CompletedAt,
                durationSeconds = job.Duration?.TotalSeconds,
                journal = job.Journal.ConvertAll(j => new
                {
                    timestamp = j.Timestamp,
                    entry = j.Entry
                })
            };
        }

        private static object FormatJobSummary(AgentJob job)
        {
            return new
            {
                id = job.Id,
                status = job.Status.ToString().ToLower(),
                promptPreview = job.Prompt.Length > 100 ? job.Prompt[..100] + "..." : job.Prompt,
                submittedAt = job.SubmittedAt,
                completedAt = job.CompletedAt,
                durationSeconds = job.Duration?.TotalSeconds
            };
        }

        private class SubmitJobRequest
        {
            public string Prompt { get; set; } = "";
            public string? Model { get; set; }
            public string? SystemPrompt { get; set; }
        }
    }

    /// <summary>
    /// Processes submitted jobs by running them through the agent loop.
    /// </summary>
    public static class AgentJobProcessor
    {
        private static Func<string, string, CancellationToken, Task<string>>? _processCallback;
        private static Func<string, string, Action<AgentStreamEvent>, CancellationToken, string?, string?, Task<string>>? _streamingCallback;

        /// <summary>
        /// Set the callback for processing jobs (called from Program.cs after agent loop is ready).
        /// </summary>
        public static void SetProcessCallback(Func<string, string, CancellationToken, Task<string>> callback)
        {
            _processCallback = callback;
        }

        /// <summary>
        /// Set the streaming callback that emits AgentStreamEvents during processing.
        /// </summary>
        public static void SetStreamingCallback(Func<string, string, Action<AgentStreamEvent>, CancellationToken, string?, string?, Task<string>> callback)
        {
            _streamingCallback = callback;
        }

        /// <summary>
        /// Process a job asynchronously with SSE event streaming.
        /// </summary>
        public static async Task ProcessJobAsync(string jobId, CancellationToken ct)
        {
            var jobService = AgentJobService.Instance;
            var job = await jobService.GetJobAsync(jobId, ct);

            if (job == null || job.Status != JobStatus.Pending)
                return;

            // Create event channel for SSE streaming
            var channel = jobService.CreateEventChannel();

            try
            {
                await jobService.StartJobAsync(ct);
                await jobService.AddJournalEntryAsync("Starting to process request...", ct);

                string? result = null;

                if (_streamingCallback != null)
                {
                    result = await _streamingCallback(jobId, job.Prompt,
                        evt => jobService.EmitEvent(evt), ct,
                        job.ModelOverride, job.SystemPromptOverride);
                }
                else if (_processCallback != null)
                {
                    result = await _processCallback(jobId, job.Prompt, ct);
                }
                else
                {
                    await jobService.FailJobAsync("Agent processor not initialized", ct);
                    jobService.CompleteEventChannel();
                    return;
                }

                // Emit complete event before closing channel
                jobService.EmitEvent(AgentStreamEvent.Complete(result ?? ""));
                await jobService.CompleteJobAsync(result ?? "", ct);
            }
            catch (OperationCanceledException)
            {
                jobService.EmitEvent(AgentStreamEvent.Error("Job cancelled"));
                await jobService.CancelJobAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                jobService.EmitEvent(AgentStreamEvent.Error(ex.Message));
                await jobService.FailJobAsync(ex.Message, CancellationToken.None);
            }
            finally
            {
                jobService.CompleteEventChannel();
            }
        }
    }
}
