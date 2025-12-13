using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace thuvu.Models
{
    /// <summary>
    /// Executes TypeScript code in a Deno sandbox with tool access via MCP bridge
    /// </summary>
    public class McpCodeExecutor : IDisposable
    {
        private readonly McpBridge _bridge;
        private readonly string _mcpPath;
        private Process? _denoProcess;
        private bool _disposed;

        public McpCodeExecutor(McpBridge? bridge = null)
        {
            _bridge = bridge ?? new McpBridge();
            
            // Find MCP directory relative to executable or current directory
            var baseDir = AppContext.BaseDirectory;
            _mcpPath = Path.Combine(baseDir, "mcp");
            
            if (!Directory.Exists(_mcpPath))
            {
                // Try current directory
                _mcpPath = Path.Combine(Directory.GetCurrentDirectory(), "mcp");
            }
        }

        /// <summary>
        /// Check if Deno is available
        /// </summary>
        public static async Task<bool> IsDenoAvailableAsync()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = McpConfig.Instance.DenoPath,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null) return false;

                await process.WaitForExitAsync();
                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Execute TypeScript code in the sandbox
        /// </summary>
        public async Task<McpExecutionResult> ExecuteAsync(
            string typeScriptCode,
            CancellationToken ct,
            TimeSpan? timeout = null)
        {
            var sw = Stopwatch.StartNew();
            var effectiveTimeout = timeout ?? TimeSpan.FromMilliseconds(McpConfig.Instance.DefaultTimeout);

            try
            {
                // Check if Deno is available
                if (!await IsDenoAvailableAsync())
                {
                    return new McpExecutionResult
                    {
                        Success = false,
                        Error = "Deno runtime not found. Please install Deno: https://deno.land",
                        Duration = sw.Elapsed
                    };
                }

                // Get permission flags
                var projectRoot = Directory.GetCurrentDirectory();
                var permissionFlags = GetPermissionFlags(projectRoot);

                // Build command arguments
                var sandboxPath = Path.Combine(_mcpPath, "runtime", "sandbox.ts");
                var args = $"run {permissionFlags} \"{sandboxPath}\"";

                var psi = new ProcessStartInfo
                {
                    FileName = McpConfig.Instance.DenoPath,
                    Arguments = args,
                    WorkingDirectory = projectRoot,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardInputEncoding = Encoding.UTF8,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                _denoProcess = Process.Start(psi);
                if (_denoProcess == null)
                {
                    return new McpExecutionResult
                    {
                        Success = false,
                        Error = "Failed to start Deno process",
                        Duration = sw.Elapsed
                    };
                }

                // Set up cancellation
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(effectiveTimeout);

                // Start reading stderr in background
                var stderrTask = ReadOutputAsync(_denoProcess.StandardError, cts.Token);

                // Send execution request
                var executionRequest = new
                {
                    code = typeScriptCode,
                    timeout = (int)effectiveTimeout.TotalMilliseconds
                };
                var requestLine = "EXECUTE:" + JsonSerializer.Serialize(executionRequest) + "\n";
                await _denoProcess.StandardInput.WriteAsync(requestLine);
                await _denoProcess.StandardInput.FlushAsync();

                // Process stdout: handle both JSON-RPC requests and collect output
                // Use single unified reader to avoid stream conflicts
                string? resultLine = null;
                var stdoutLines = new List<string>();
                
                try
                {
                    while (!_denoProcess.HasExited || !_denoProcess.StandardOutput.EndOfStream)
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        
                        var line = await _denoProcess.StandardOutput.ReadLineAsync(cts.Token);
                        if (line == null) break;
                        
                        stdoutLines.Add(line);
                        
                        // Check if it's the result
                        if (line.StartsWith("RESULT:"))
                        {
                            resultLine = line;
                            continue;
                        }
                        
                        // Check if it's a JSON-RPC request from sandbox
                        if (line.StartsWith("{"))
                        {
                            try
                            {
                                var request = JsonSerializer.Deserialize<JsonRpcRequest>(line, new JsonSerializerOptions
                                {
                                    PropertyNameCaseInsensitive = true
                                });

                                if (request != null)
                                {
                                    var response = await _bridge.HandleRequestAsync(request, cts.Token);
                                    var responseLine = JsonSerializer.Serialize(response, new JsonSerializerOptions
                                    {
                                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                                    }) + "\n";

                                    await _denoProcess.StandardInput.WriteAsync(responseLine);
                                    await _denoProcess.StandardInput.FlushAsync();
                                }
                            }
                            catch (JsonException)
                            {
                                // Not a JSON-RPC request, ignore
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    _denoProcess.Kill(entireProcessTree: true);
                    return new McpExecutionResult
                    {
                        Success = false,
                        Error = "Execution timed out",
                        Duration = sw.Elapsed,
                        ToolCalls = _bridge.GetAndClearLogs()
                    };
                }

                // Wait for process to fully exit
                await _denoProcess.WaitForExitAsync();
                var stderr = await stderrTask;

                if (resultLine != null)
                {
                    var resultJson = resultLine["RESULT:".Length..];
                    using var doc = JsonDocument.Parse(resultJson);
                    var root = doc.RootElement;

                    var success = root.TryGetProperty("success", out var successEl) && successEl.GetBoolean();
                    var result = root.TryGetProperty("result", out var resultEl) 
                        ? resultEl.ToString() 
                        : null;
                    var error = root.TryGetProperty("error", out var errorEl)
                        ? errorEl.GetString()
                        : null;

                    sw.Stop();
                    return new McpExecutionResult
                    {
                        Success = success,
                        Result = result,
                        Error = error,
                        Duration = sw.Elapsed,
                        ToolCalls = _bridge.GetAndClearLogs()
                    };
                }

                sw.Stop();
                return new McpExecutionResult
                {
                    Success = _denoProcess.ExitCode == 0,
                    Result = string.Join("\n", stdoutLines),
                    Error = stderr.Length > 0 ? stderr : null,
                    Duration = sw.Elapsed,
                    ToolCalls = _bridge.GetAndClearLogs()
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new McpExecutionResult
                {
                    Success = false,
                    Error = ex.Message,
                    Duration = sw.Elapsed,
                    ToolCalls = _bridge.GetAndClearLogs()
                };
            }
            finally
            {
                _denoProcess?.Dispose();
                _denoProcess = null;
            }
        }

        /// <summary>
        /// Read all output from a stream
        /// </summary>
        private static async Task<string> ReadOutputAsync(StreamReader reader, CancellationToken ct)
        {
            var sb = new StringBuilder();
            var buffer = new char[4096];
            int read;

            while ((read = await reader.ReadAsync(buffer, ct)) > 0)
            {
                sb.Append(buffer, 0, read);
            }

            return sb.ToString();
        }

        /// <summary>
        /// Get Deno permission flags based on config
        /// </summary>
        private string GetPermissionFlags(string projectRoot)
        {
            var config = McpConfig.Instance;
            var flags = new StringBuilder();

            switch (config.PermissionLevel.ToLowerInvariant())
            {
                case "readonly":
                    flags.Append($"--allow-read=\"{projectRoot}\" ");
                    break;

                case "readwrite":
                    flags.Append($"--allow-read=\"{projectRoot}\" ");
                    flags.Append($"--allow-write=\"{projectRoot}\" ");
                    break;

                case "execute":
                    flags.Append($"--allow-read=\"{projectRoot}\" ");
                    flags.Append($"--allow-write=\"{projectRoot}\" ");
                    flags.Append("--allow-run=dotnet,git,npm,node ");
                    break;

                case "full":
                    flags.Append("--allow-all ");
                    break;

                default:
                    flags.Append($"--allow-read=\"{projectRoot}\" ");
                    flags.Append($"--allow-write=\"{projectRoot}\" ");
                    break;
            }

            // Always deny network unless full permissions
            if (config.PermissionLevel.ToLowerInvariant() != "full")
            {
                flags.Append("--deny-net ");
            }

            return flags.ToString().Trim();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_denoProcess != null && !_denoProcess.HasExited)
            {
                try
                {
                    _denoProcess.Kill(entireProcessTree: true);
                }
                catch { }
            }
            _denoProcess?.Dispose();
        }
    }
}
