using System.Diagnostics;
using System.Text.Json;

namespace thuvu.Services;

/// <summary>
/// Discovers running agent servers from the local registry at ~/.thuvu/agents/.
/// Used by CLI/TUI/Desktop to auto-connect to an existing server.
/// </summary>
public static class AgentServerLocator
{
    private static readonly string RegistryDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".thuvu", "agents");

    /// <summary>
    /// Find a running agent server. Returns (url, token) or null if none found.
    /// </summary>
    public static async Task<AgentServerInfo?> FindRunningServerAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(RegistryDir))
            return null;

        var files = Directory.GetFiles(RegistryDir, "*.json");
        foreach (var file in files.OrderByDescending(File.GetLastWriteTimeUtc))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file, ct);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var pid = root.TryGetProperty("pid", out var p) ? p.GetInt32() : 0;
                var port = root.TryGetProperty("port", out var po) ? po.GetInt32() : 0;
                var token = root.TryGetProperty("token", out var t) ? t.GetString() : null;

                if (pid <= 0 || port <= 0) continue;

                // Check if process is still alive
                try
                {
                    var proc = Process.GetProcessById(pid);
                    if (proc.HasExited)
                    {
                        CleanupStaleEntry(file);
                        continue;
                    }
                }
                catch
                {
                    CleanupStaleEntry(file);
                    continue;
                }

                var url = $"http://localhost:{port}";

                // Verify the server is actually responding
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                if (!string.IsNullOrEmpty(token))
                    http.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

                var resp = await http.GetAsync($"{url}/api/health", ct);
                if (resp.IsSuccessStatusCode)
                {
                    return new AgentServerInfo
                    {
                        Url = url,
                        Token = token,
                        Pid = pid,
                        Port = port,
                        RegistryFile = file
                    };
                }
            }
            catch
            {
                // Skip invalid entries
            }
        }

        return null;
    }

    /// <summary>
    /// Spawn a new agent server process and wait for it to be ready.
    /// Returns the server info or null if spawn failed.
    /// </summary>
    public static async Task<AgentServerInfo?> SpawnServerAsync(
        string? configPath = null,
        int port = 0,
        CancellationToken ct = default)
    {
        // Find a free port if not specified
        if (port == 0)
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
        }

        var token = Guid.NewGuid().ToString("N");

        // Find the thuvu executable
        var exePath = FindThuVuExecutable();
        if (exePath == null)
            return null;

        var args = $"--api --port {port}";
        if (!string.IsNullOrEmpty(configPath))
            args += $" --config \"{configPath}\"";

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };

        // Pass token via environment variable
        psi.EnvironmentVariables["THUVU_BEARER_TOKEN"] = token;

        var process = Process.Start(psi);
        if (process == null) return null;

        // Wait for server to become ready (poll health endpoint)
        var url = $"http://localhost:{port}";
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        for (int i = 0; i < 30; i++) // Wait up to 30 seconds
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(1000, ct);

            if (process.HasExited) return null;

            try
            {
                var resp = await http.GetAsync($"{url}/api/health", ct);
                if (resp.IsSuccessStatusCode)
                {
                    return new AgentServerInfo
                    {
                        Url = url,
                        Token = token,
                        Pid = process.Id,
                        Port = port
                    };
                }
            }
            catch { }
        }

        // Timeout — kill the process
        try { process.Kill(); } catch { }
        return null;
    }

    private static string? FindThuVuExecutable()
    {
        // Check common locations
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "thuvu.exe"),
            Path.Combine(AppContext.BaseDirectory, "thuvu"),
            "thuvu" // PATH lookup
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        // Try dotnet run as fallback
        return null;
    }

    private static void CleanupStaleEntry(string file)
    {
        try { File.Delete(file); } catch { }
    }
}

public class AgentServerInfo
{
    public string Url { get; set; } = "";
    public string? Token { get; set; }
    public int Pid { get; set; }
    public int Port { get; set; }
    public string? RegistryFile { get; set; }
}
