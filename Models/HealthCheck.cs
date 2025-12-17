using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace thuvu.Models
{
    /// <summary>
    /// Health check status for a single component
    /// </summary>
    public class HealthCheckResult
    {
        public string Component { get; set; } = "";
        public string Endpoint { get; set; } = "";
        public bool IsHealthy { get; set; }
        public string Status { get; set; } = "";
        public string? Warning { get; set; }
        public string? Error { get; set; }
        public TimeSpan Duration { get; set; }
    }

    /// <summary>
    /// Overall health check results
    /// </summary>
    public class HealthCheckReport
    {
        public bool AllHealthy => Results.TrueForAll(r => r.IsHealthy);
        public bool CanStart => CriticalHealthy;
        public bool CriticalHealthy => LmStudio && Git && WorkDirectory;
        
        // Individual component status
        public bool LmStudio { get; set; }
        public bool ModelLoaded { get; set; }
        public bool Git { get; set; }
        public bool Deno { get; set; }
        public bool PostgreSql { get; set; }
        public bool WorkDirectory { get; set; }
        
        public List<HealthCheckResult> Results { get; set; } = new();
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// Performs health checks on all required services
    /// </summary>
    public static class HealthCheck
    {
        /// <summary>
        /// Run all health checks
        /// </summary>
        public static async Task<HealthCheckReport> RunAllChecksAsync(HttpClient http, CancellationToken ct = default)
        {
            var report = new HealthCheckReport();
            
            // Run checks in parallel for speed
            var tasks = new List<Task<HealthCheckResult>>
            {
                CheckLmStudioAsync(http, ct),
                CheckGitAsync(ct),
                CheckWorkDirectoryAsync(),
                CheckDenoAsync(ct),
                CheckPostgreSqlAsync(ct)
            };

            var results = await Task.WhenAll(tasks);
            report.Results.AddRange(results);

            // Set individual flags
            foreach (var result in results)
            {
                switch (result.Component)
                {
                    case "LM Studio":
                        report.LmStudio = result.IsHealthy;
                        break;
                    case "Model":
                        report.ModelLoaded = result.IsHealthy;
                        break;
                    case "Git":
                        report.Git = result.IsHealthy;
                        break;
                    case "Deno":
                        report.Deno = result.IsHealthy;
                        break;
                    case "PostgreSQL":
                        report.PostgreSql = result.IsHealthy;
                        break;
                    case "Work Directory":
                        report.WorkDirectory = result.IsHealthy;
                        break;
                }
            }

            return report;
        }

        /// <summary>
        /// Check if LM Studio is running and a model is loaded
        /// </summary>
        public static async Task<HealthCheckResult> CheckLmStudioAsync(HttpClient http, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var result = new HealthCheckResult
            {
                Component = "LM Studio",
                Endpoint = AgentConfig.Config.HostUrl
            };

            try
            {
                // Try to get models list
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);
                
                var response = await http.GetAsync("/v1/models", linkedCts.Token);
                
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(linkedCts.Token);
                    using var doc = JsonDocument.Parse(json);
                    
                    if (doc.RootElement.TryGetProperty("data", out var data) && data.GetArrayLength() > 0)
                    {
                        // Check if configured model is loaded
                        var configuredModel = AgentConfig.Config.Model;
                        bool modelFound = false;
                        var availableModels = new List<string>();
                        
                        foreach (var model in data.EnumerateArray())
                        {
                            if (model.TryGetProperty("id", out var idProp))
                            {
                                var modelId = idProp.GetString() ?? "";
                                availableModels.Add(modelId);
                                if (modelId.Equals(configuredModel, StringComparison.OrdinalIgnoreCase))
                                {
                                    modelFound = true;
                                }
                            }
                        }

                        result.IsHealthy = true;
                        if (modelFound)
                        {
                            result.Status = $"Connected, {configuredModel} loaded";
                        }
                        else
                        {
                            result.Status = "Connected";
                            result.Warning = $"Model '{configuredModel}' not found. Available: {string.Join(", ", availableModels.Take(3))}";
                        }
                    }
                    else
                    {
                        result.IsHealthy = true;
                        result.Status = "Connected";
                        result.Warning = "No models loaded";
                    }
                }
                else
                {
                    result.IsHealthy = false;
                    result.Status = "Error";
                    result.Error = $"HTTP {(int)response.StatusCode}";
                }
            }
            catch (TaskCanceledException)
            {
                result.IsHealthy = false;
                result.Status = "Timeout";
                result.Error = "Connection timed out (5s)";
            }
            catch (HttpRequestException ex)
            {
                result.IsHealthy = false;
                result.Status = "Not running";
                result.Error = ex.Message;
            }
            catch (Exception ex)
            {
                result.IsHealthy = false;
                result.Status = "Error";
                result.Error = ex.Message;
            }

            sw.Stop();
            result.Duration = sw.Elapsed;
            return result;
        }

        /// <summary>
        /// Check if Git is installed and accessible
        /// </summary>
        public static async Task<HealthCheckResult> CheckGitAsync(CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var result = new HealthCheckResult
            {
                Component = "Git",
                Endpoint = "git --version"
            };

            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "git",
                        Arguments = "--version",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                var output = await process.StandardOutput.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);

                if (process.ExitCode == 0)
                {
                    var version = output.Trim();
                    result.IsHealthy = true;
                    result.Status = version;
                    
                    // Check if we're in a git repo
                    var workDir = AgentConfig.GetWorkDirectory();
                    if (!Directory.Exists(Path.Combine(workDir, ".git")))
                    {
                        result.Warning = "Work directory is not a git repository";
                    }
                }
                else
                {
                    result.IsHealthy = false;
                    result.Status = "Error";
                    result.Error = $"Exit code {process.ExitCode}";
                }
            }
            catch (Exception ex)
            {
                result.IsHealthy = false;
                result.Status = "Not installed";
                result.Error = ex.Message;
            }

            sw.Stop();
            result.Duration = sw.Elapsed;
            return result;
        }

        /// <summary>
        /// Check if Deno is installed (optional for MCP)
        /// </summary>
        public static async Task<HealthCheckResult> CheckDenoAsync(CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var result = new HealthCheckResult
            {
                Component = "Deno",
                Endpoint = McpConfig.Instance.DenoPath
            };

            // Deno is optional - only check if MCP is enabled
            if (!McpConfig.Instance.Enabled)
            {
                result.IsHealthy = true;
                result.Status = "Disabled (MCP off)";
                sw.Stop();
                result.Duration = sw.Elapsed;
                return result;
            }

            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = McpConfig.Instance.DenoPath,
                        Arguments = "--version",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                var output = await process.StandardOutput.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);

                if (process.ExitCode == 0)
                {
                    var firstLine = output.Split('\n')[0].Trim();
                    result.IsHealthy = true;
                    result.Status = firstLine;
                }
                else
                {
                    result.IsHealthy = false;
                    result.Status = "Error";
                    result.Error = $"Exit code {process.ExitCode}";
                }
            }
            catch (Exception ex)
            {
                result.IsHealthy = false;
                result.Status = "Not installed";
                result.Error = $"MCP disabled: {ex.Message}";
            }

            sw.Stop();
            result.Duration = sw.Elapsed;
            return result;
        }

        /// <summary>
        /// Check PostgreSQL connection (optional for RAG)
        /// </summary>
        public static async Task<HealthCheckResult> CheckPostgreSqlAsync(CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var result = new HealthCheckResult
            {
                Component = "PostgreSQL",
                Endpoint = "localhost:5433"
            };

            // PostgreSQL is optional - only check if RAG is enabled
            if (!RagConfig.Instance.Enabled)
            {
                result.IsHealthy = true;
                result.Status = "Disabled (RAG off)";
                sw.Stop();
                result.Duration = sw.Elapsed;
                return result;
            }

            try
            {
                // Parse connection string for host/port
                var connStr = RagConfig.Instance.ConnectionString;
                var hostMatch = System.Text.RegularExpressions.Regex.Match(connStr, @"Host=([^;]+)");
                var portMatch = System.Text.RegularExpressions.Regex.Match(connStr, @"Port=(\d+)");
                
                var host = hostMatch.Success ? hostMatch.Groups[1].Value : "localhost";
                var port = portMatch.Success ? int.Parse(portMatch.Groups[1].Value) : 5432;
                result.Endpoint = $"{host}:{port}";

                // Try to connect using Npgsql
                using var conn = new Npgsql.NpgsqlConnection(connStr);
                await conn.OpenAsync(ct);
                
                // Check if pgvector extension is installed
                using var cmd = new Npgsql.NpgsqlCommand("SELECT extversion FROM pg_extension WHERE extname = 'vector'", conn);
                var version = await cmd.ExecuteScalarAsync(ct);
                
                if (version != null)
                {
                    result.IsHealthy = true;
                    result.Status = $"Connected, pgvector {version}";
                }
                else
                {
                    result.IsHealthy = true;
                    result.Status = "Connected";
                    result.Warning = "pgvector extension not installed";
                }
            }
            catch (Exception ex)
            {
                result.IsHealthy = false;
                result.Status = "Not connected";
                result.Error = $"RAG disabled: {ex.Message}";
            }

            sw.Stop();
            result.Duration = sw.Elapsed;
            return result;
        }

        /// <summary>
        /// Check work directory is accessible and writable
        /// </summary>
        public static Task<HealthCheckResult> CheckWorkDirectoryAsync()
        {
            var sw = Stopwatch.StartNew();
            var result = new HealthCheckResult
            {
                Component = "Work Directory",
                Endpoint = AgentConfig.Config.WorkDirectory
            };

            try
            {
                var workDir = AgentConfig.GetWorkDirectory();
                result.Endpoint = workDir;

                // Check if directory exists (GetWorkDirectory creates it)
                if (!Directory.Exists(workDir))
                {
                    result.IsHealthy = false;
                    result.Status = "Not found";
                    result.Error = "Directory does not exist";
                }
                else
                {
                    // Try to write a test file
                    var testFile = Path.Combine(workDir, ".thuvu_health_check");
                    try
                    {
                        File.WriteAllText(testFile, DateTime.Now.ToString());
                        File.Delete(testFile);
                        result.IsHealthy = true;
                        result.Status = "Writable";
                    }
                    catch (Exception ex)
                    {
                        result.IsHealthy = false;
                        result.Status = "Not writable";
                        result.Error = ex.Message;
                    }
                }
            }
            catch (Exception ex)
            {
                result.IsHealthy = false;
                result.Status = "Error";
                result.Error = ex.Message;
            }

            sw.Stop();
            result.Duration = sw.Elapsed;
            return Task.FromResult(result);
        }

        /// <summary>
        /// Print health check report to console
        /// </summary>
        public static void PrintReport(HealthCheckReport report)
        {
            const int boxWidth = 72;
            var line = new string('═', boxWidth - 2);

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"╔{line}╗");
            PrintBoxLine("Health Check Results", boxWidth);
            Console.WriteLine($"╠{line}╣");
            Console.ResetColor();

            foreach (var result in report.Results)
            {
                // Use simple ASCII for consistent width (all 4 chars)
                var icon = result.IsHealthy ? "[OK]" : "[XX]";
                if (result.IsHealthy && result.Warning != null)
                    icon = "[!!]";

                var component = Truncate(result.Component, 14).PadRight(14);
                var endpoint = Truncate(result.Endpoint, 24).PadRight(24);
                var status = Truncate(result.Status, 18).PadRight(18);

                // Build the line content and calculate padding
                // Format: "║  [OK] Component       Endpoint                 Status             ║"
                var content = $"  {icon} {component} {endpoint} {status}";
                var padding = boxWidth - 2 - content.Length; // -2 for "║" on each side

                Console.Write("║");
                Console.Write("  ");
                Console.ForegroundColor = result.IsHealthy ? ConsoleColor.Green : ConsoleColor.Red;
                Console.Write(icon);
                Console.ResetColor();
                Console.Write(" ");
                
                Console.ForegroundColor = result.IsHealthy ? ConsoleColor.White : ConsoleColor.Red;
                Console.Write(component);
                Console.ResetColor();
                
                Console.Write(" ");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write(endpoint);
                Console.ResetColor();
                
                Console.Write(" ");
                Console.ForegroundColor = result.IsHealthy ? ConsoleColor.Green : ConsoleColor.Red;
                Console.Write(status);
                Console.ResetColor();
                
                Console.Write(new string(' ', Math.Max(0, padding)));
                Console.WriteLine("║");

                // Print warning or error on next line if present
                if (result.Warning != null)
                {
                    PrintDetailLine("! " + result.Warning, boxWidth, ConsoleColor.Yellow);
                }
                else if (result.Error != null && !result.IsHealthy)
                {
                    PrintDetailLine("> " + result.Error, boxWidth, ConsoleColor.Red);
                }
            }

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"╠{line}╣");
            Console.ResetColor();

            // Summary
            if (report.CanStart)
            {
                PrintBoxLine("[OK] Ready to start", boxWidth, ConsoleColor.Green);
            }
            else
            {
                PrintBoxLine("[X] Cannot start - fix critical issues above", boxWidth, ConsoleColor.Red);
            }

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"╚{line}╝");
            Console.ResetColor();
            Console.WriteLine();
        }

        /// <summary>
        /// Truncate string to max length with ellipsis
        /// </summary>
        private static string Truncate(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (text.Length <= maxLength) return text;
            return text.Substring(0, maxLength - 3) + "...";
        }

        /// <summary>
        /// Print a line inside the box with proper padding
        /// </summary>
        private static void PrintBoxLine(string text, int boxWidth, ConsoleColor? color = null)
        {
            // boxWidth = 72, line is: ║ + 70 inner chars + ║
            // Inner format: "  " (2) + text + padding = 70 chars
            // So textArea = 70 - 2 = 68 chars for text + padding
            var innerWidth = boxWidth - 2; // 70
            var textArea = innerWidth - 2;  // 68 (subtract "  " prefix)
            var truncated = Truncate(text, textArea);
            var padding = textArea - truncated.Length;
            
            Console.Write("║  ");
            if (color.HasValue) Console.ForegroundColor = color.Value;
            Console.Write(truncated);
            if (color.HasValue) Console.ResetColor();
            Console.Write(new string(' ', padding));
            Console.WriteLine("║");
        }

        /// <summary>
        /// Print a detail/error line with indent
        /// </summary>
        private static void PrintDetailLine(string text, int boxWidth, ConsoleColor color)
        {
            // Inner format: "     " (5) + text + padding = 70 chars
            // So textArea = 70 - 5 = 65 chars for text + padding
            var innerWidth = boxWidth - 2; // 70
            var textArea = innerWidth - 5;  // 65 (subtract "     " prefix)
            var truncated = Truncate(text, textArea);
            var padding = textArea - truncated.Length;
            
            Console.Write("║     ");
            Console.ForegroundColor = color;
            Console.Write(truncated);
            Console.ResetColor();
            Console.Write(new string(' ', padding));
            Console.WriteLine("║");
        }
    }
}
