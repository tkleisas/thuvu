using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using thuvu.Models;

namespace thuvu.Tools
{
    /// <summary>
    /// Tools for agent-to-agent communication.
    /// </summary>
    public static class AgentCommunicationToolImpl
    {
        private static readonly HttpClient _httpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        /// <summary>
        /// List all known agents and their status.
        /// </summary>
        public static async Task<string> AgentListAsync(CancellationToken ct = default)
        {
            try
            {
                var agents = AgentApiConfig.Instance.KnownAgents;
                var results = new List<object>();

                foreach (var agent in agents)
                {
                    try
                    {
                        var info = await GetAgentInfoAsync(agent, ct);
                        results.Add(new
                        {
                            name = agent.Name,
                            url = agent.Url,
                            description = agent.Description,
                            status = info?.Status ?? "unknown",
                            currentJob = info?.CurrentJobId,
                            online = info != null
                        });
                    }
                    catch
                    {
                        results.Add(new
                        {
                            name = agent.Name,
                            url = agent.Url,
                            description = agent.Description,
                            status = "offline",
                            currentJob = (string?)null,
                            online = false
                        });
                    }
                }

                return JsonSerializer.Serialize(new
                {
                    success = true,
                    agents = results
                }, _jsonOptions);
            }
            catch (Exception ex)
            {
                AgentLogger.LogError(ex, "agent_list failed");
                return JsonSerializer.Serialize(new { success = false, error = ex.Message }, _jsonOptions);
            }
        }

        /// <summary>
        /// Submit a prompt to a named agent.
        /// </summary>
        public static async Task<string> AgentSubmitAsync(string agentName, string prompt, CancellationToken ct = default)
        {
            try
            {
                var agent = FindAgent(agentName);
                if (agent == null)
                {
                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = $"Unknown agent: {agentName}. Use agent_list to see available agents."
                    }, _jsonOptions);
                }

                var request = new HttpRequestMessage(HttpMethod.Post, $"{agent.Url}/api/jobs");
                ApplyAuth(request, agent);
                request.Content = new StringContent(
                    JsonSerializer.Serialize(new { prompt }, _jsonOptions),
                    Encoding.UTF8,
                    "application/json");

                var response = await _httpClient.SendAsync(request, ct);
                var content = await response.Content.ReadAsStringAsync(ct);

                if (response.IsSuccessStatusCode)
                {
                    return content;
                }
                else
                {
                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = $"Agent returned {response.StatusCode}: {content}"
                    }, _jsonOptions);
                }
            }
            catch (Exception ex)
            {
                AgentLogger.LogError(ex, "agent_submit failed");
                return JsonSerializer.Serialize(new { success = false, error = ex.Message }, _jsonOptions);
            }
        }

        /// <summary>
        /// Get the current job status from an agent.
        /// </summary>
        public static async Task<string> AgentStatusAsync(string agentName, CancellationToken ct = default)
        {
            try
            {
                var agent = FindAgent(agentName);
                if (agent == null)
                {
                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = $"Unknown agent: {agentName}"
                    }, _jsonOptions);
                }

                var request = new HttpRequestMessage(HttpMethod.Get, $"{agent.Url}/api/jobs/current");
                ApplyAuth(request, agent);

                var response = await _httpClient.SendAsync(request, ct);
                var content = await response.Content.ReadAsStringAsync(ct);

                if (response.IsSuccessStatusCode)
                {
                    return content;
                }
                else
                {
                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = $"Agent returned {response.StatusCode}: {content}"
                    }, _jsonOptions);
                }
            }
            catch (Exception ex)
            {
                AgentLogger.LogError(ex, "agent_status failed");
                return JsonSerializer.Serialize(new { success = false, error = ex.Message }, _jsonOptions);
            }
        }

        /// <summary>
        /// Get a specific job result from an agent.
        /// </summary>
        public static async Task<string> AgentResultAsync(string agentName, string jobId, CancellationToken ct = default)
        {
            try
            {
                var agent = FindAgent(agentName);
                if (agent == null)
                {
                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = $"Unknown agent: {agentName}"
                    }, _jsonOptions);
                }

                var request = new HttpRequestMessage(HttpMethod.Get, $"{agent.Url}/api/jobs/{jobId}");
                ApplyAuth(request, agent);

                var response = await _httpClient.SendAsync(request, ct);
                var content = await response.Content.ReadAsStringAsync(ct);

                if (response.IsSuccessStatusCode)
                {
                    return content;
                }
                else
                {
                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = $"Agent returned {response.StatusCode}: {content}"
                    }, _jsonOptions);
                }
            }
            catch (Exception ex)
            {
                AgentLogger.LogError(ex, "agent_result failed");
                return JsonSerializer.Serialize(new { success = false, error = ex.Message }, _jsonOptions);
            }
        }

        /// <summary>
        /// Cancel a job on an agent.
        /// </summary>
        public static async Task<string> AgentCancelAsync(string agentName, string jobId, CancellationToken ct = default)
        {
            try
            {
                var agent = FindAgent(agentName);
                if (agent == null)
                {
                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = $"Unknown agent: {agentName}"
                    }, _jsonOptions);
                }

                var request = new HttpRequestMessage(HttpMethod.Delete, $"{agent.Url}/api/jobs/{jobId}");
                ApplyAuth(request, agent);

                var response = await _httpClient.SendAsync(request, ct);
                var content = await response.Content.ReadAsStringAsync(ct);

                if (response.IsSuccessStatusCode)
                {
                    return content;
                }
                else
                {
                    return JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = $"Agent returned {response.StatusCode}: {content}"
                    }, _jsonOptions);
                }
            }
            catch (Exception ex)
            {
                AgentLogger.LogError(ex, "agent_cancel failed");
                return JsonSerializer.Serialize(new { success = false, error = ex.Message }, _jsonOptions);
            }
        }

        private static KnownAgent? FindAgent(string name)
        {
            return AgentApiConfig.Instance.KnownAgents
                .Find(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        private static void ApplyAuth(HttpRequestMessage request, KnownAgent agent)
        {
            if (!string.IsNullOrEmpty(agent.Token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", agent.Token);
            }
        }

        private static async Task<AgentInfoResponse?> GetAgentInfoAsync(KnownAgent agent, CancellationToken ct)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{agent.Url}/api/agent/info");
            ApplyAuth(request, agent);

            var response = await _httpClient.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(ct);
                return JsonSerializer.Deserialize<AgentInfoResponse>(content, _jsonOptions);
            }
            return null;
        }

        private class AgentInfoResponse
        {
            public string? Name { get; set; }
            public string? Status { get; set; }
            public string? CurrentJobId { get; set; }
        }
    }
}
