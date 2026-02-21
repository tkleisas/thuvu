using System.Diagnostics;
using System.Text.Json;
using thuvu.Models;

namespace thuvu.Desktop.Services;

/// <summary>
/// Manages spawning, tracking, and discovering detached agent processes.
/// Each agent writes a registry file to .db/agents/ for discovery on restart.
/// </summary>
public class AgentProcessManager
{
    private static AgentProcessManager? _instance;
    public static AgentProcessManager Instance => _instance ??= new AgentProcessManager();

    private readonly Dictionary<string, AgentProcessInfo> _processes = new();
    private string? _registryDir;
    private string? _thuvuExePath;
    private string? _projectDir;

    /// <summary>All known agent processes (running or stale)</summary>
    public IReadOnlyDictionary<string, AgentProcessInfo> Processes => _processes;

    /// <summary>
    /// Initialize with project directory. Creates .db/agents/ if needed.
    /// </summary>
    public void Initialize(string projectDir, string? thuvuExePath = null)
    {
        _projectDir = projectDir;
        _registryDir = Path.Combine(projectDir, ".db", "agents");
        Directory.CreateDirectory(_registryDir);
        _thuvuExePath = thuvuExePath ?? FindThuvuExe();
        AgentLogger.LogInfo("AgentProcessManager initialized. Exe: {Exe}, Registry: {Dir}",
            _thuvuExePath ?? "(not found)", _registryDir);
        DiscoverExistingAgents();
    }

    /// <summary>
    /// Spawn a new detached agent process.
    /// </summary>
    public async Task<AgentProcessInfo?> SpawnAgentAsync(string agentId, string? name = null)
    {
        var diagLog = _registryDir != null ? Path.Combine(_registryDir, "spawn_diag.log") : null;
        void Diag(string msg)
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
            AgentLogger.LogInfo(line);
            if (diagLog != null) try { File.AppendAllText(diagLog, line + "\n"); } catch { }
        }

        Diag($"SpawnAgentAsync called. ExePath={_thuvuExePath}, Exists={(_thuvuExePath != null && File.Exists(_thuvuExePath))}");

        if (_thuvuExePath == null || !File.Exists(_thuvuExePath))
        {
            Diag("FAIL: thuvu executable not found");
            return null;
        }

        var port = FindAvailablePort();
        var token = GenerateToken();

        // Pass the Desktop's config file so the spawned agent inherits model/auth settings
        var configPath = AgentConfig.GetConfigPath();
        var configArg = File.Exists(configPath) ? $" --config \"{configPath}\"" : "";

        Diag($"Port={port}, WorkDir={_projectDir}, Config={configPath}, Args=--api --port {port}{configArg}");

        var startInfo = new ProcessStartInfo
        {
            FileName = _thuvuExePath,
            Arguments = $"--api --port {port}{configArg}",
            WorkingDirectory = _projectDir ?? Directory.GetCurrentDirectory(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // Pass token via environment variable for security
        startInfo.EnvironmentVariables["THUVU_API_TOKEN"] = token;

        Process process;
        try
        {
            process = Process.Start(startInfo)!;
            Diag($"Process started, PID={process.Id}");
        }
        catch (Exception ex)
        {
            Diag($"FAIL: Process.Start threw: {ex.Message}");
            return null;
        }

        var info = new AgentProcessInfo
        {
            AgentId = agentId,
            Name = name ?? agentId,
            Pid = process.Id,
            Port = port,
            Token = token,
            Url = $"http://localhost:{port}",
            StartTime = DateTime.UtcNow,
            ExePath = _thuvuExePath
        };

        // Wait for the server to start, checking if process crashed
        var connected = false;
        for (int i = 0; i < 15; i++)
        {
            await Task.Delay(1000);

            if (process.HasExited)
            {
                var stderr = await process.StandardError.ReadToEndAsync();
                Diag($"FAIL: Process exited with code {process.ExitCode}. Stderr: {(stderr.Length > 500 ? stderr[..500] : stderr)}");
                return null;
            }

            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                if (!string.IsNullOrEmpty(token))
                    http.DefaultRequestHeaders.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                var resp = await http.GetAsync($"{info.Url}/api/agent/info");
                Diag($"Health check {i+1}: {resp.StatusCode}");
                if (resp.IsSuccessStatusCode)
                {
                    connected = true;
                    break;
                }
            }
            catch (Exception ex) { Diag($"Health check {i+1}: {ex.GetType().Name} - {ex.Message}"); }
        }

        if (!connected)
        {
            var stderr = "";
            try { stderr = process.StandardError.ReadToEnd(); } catch { }
            Diag($"FAIL: Not responding after 15s. PID={process.Id}, Port={port}. Stderr: {(stderr.Length > 500 ? stderr[..500] : stderr)}");
            try { process.Kill(); } catch { }
            return null;
        }

        _processes[agentId] = info;
        WriteRegistryFile(info);

        Diag($"SUCCESS: Agent '{name}' spawned on port {port}, PID={info.Pid}");
        return info;
    }

    /// <summary>
    /// Stop a running agent process.
    /// </summary>
    public void StopAgent(string agentId)
    {
        if (!_processes.TryGetValue(agentId, out var info)) return;

        try
        {
            var process = Process.GetProcessById(info.Pid);
            process.Kill(entireProcessTree: true);
        }
        catch { }

        _processes.Remove(agentId);
        DeleteRegistryFile(agentId);
    }

    /// <summary>
    /// Check which registered agents are still alive, remove stale entries.
    /// </summary>
    public void CleanupStaleAgents()
    {
        var stale = new List<string>();
        foreach (var (id, info) in _processes)
        {
            if (!IsProcessAlive(info.Pid))
                stale.Add(id);
        }

        foreach (var id in stale)
        {
            _processes.Remove(id);
            DeleteRegistryFile(id);
            AgentLogger.LogInfo("Cleaned up stale agent '{AgentId}'", id);
        }
    }

    /// <summary>
    /// Get info for connecting to an existing agent.
    /// Returns null if agent is not running.
    /// </summary>
    public AgentProcessInfo? GetRunningAgent(string agentId)
    {
        if (!_processes.TryGetValue(agentId, out var info)) return null;
        if (!IsProcessAlive(info.Pid))
        {
            _processes.Remove(agentId);
            DeleteRegistryFile(agentId);
            return null;
        }
        return info;
    }

    /// <summary>
    /// Create a RemoteAgentService connected to an existing agent process.
    /// </summary>
    public RemoteAgentService? CreateRemoteService(string agentId)
    {
        var info = GetRunningAgent(agentId);
        if (info == null) return null;

        return new RemoteAgentService(info.Url, info.Token)
        {
            SessionId = agentId
        };
    }

    // --- Discovery ---

    private void DiscoverExistingAgents()
    {
        if (_registryDir == null || !Directory.Exists(_registryDir)) return;

        foreach (var file in Directory.GetFiles(_registryDir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var info = JsonSerializer.Deserialize<AgentProcessInfo>(json);
                if (info != null && IsProcessAlive(info.Pid))
                {
                    _processes[info.AgentId] = info;
                    AgentLogger.LogInfo("Discovered running agent '{Name}' (PID {Pid})", info.Name, info.Pid);
                }
                else
                {
                    // Stale file
                    File.Delete(file);
                }
            }
            catch
            {
                try { File.Delete(file); } catch { }
            }
        }
    }

    // --- Registry file I/O ---

    private void WriteRegistryFile(AgentProcessInfo info)
    {
        if (_registryDir == null) return;
        var path = Path.Combine(_registryDir, $"{info.AgentId}.json");
        var json = JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    private void DeleteRegistryFile(string agentId)
    {
        if (_registryDir == null) return;
        var path = Path.Combine(_registryDir, $"{agentId}.json");
        try { File.Delete(path); } catch { }
    }

    // --- Helpers ---

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch { return false; }
    }

    private static int FindAvailablePort()
    {
        // Find a free port by binding to port 0
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string GenerateToken()
    {
        var bytes = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).Replace("+", "").Replace("/", "").Replace("=", "");
    }

    private static string? FindThuvuExe()
    {
        // Look for thuvu executable in common locations
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "thuvu.exe"),
            Path.Combine(AppContext.BaseDirectory, "thuvu"),
            "thuvu"  // PATH lookup
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        // Try to find via build output relative to solution root
        var solutionDir = FindSolutionDirectory();
        if (solutionDir != null)
        {
            // Search all framework/config combinations
            var binDir = Path.Combine(solutionDir, "bin");
            if (Directory.Exists(binDir))
            {
                try
                {
                    var found = Directory.GetFiles(binDir, "thuvu.exe", SearchOption.AllDirectories);
                    if (found.Length > 0)
                    {
                        // Prefer Debug over Release
                        var debug = found.FirstOrDefault(f => f.Contains("Debug"));
                        return debug ?? found[0];
                    }
                }
                catch { }
            }
        }

        return null;
    }

    private static string? FindSolutionDirectory()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "thuvu.sln")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}

/// <summary>
/// Information about a spawned agent process, persisted to .db/agents/.
/// </summary>
public class AgentProcessInfo
{
    public string AgentId { get; set; } = "";
    public string Name { get; set; } = "";
    public int Pid { get; set; }
    public int Port { get; set; }
    public string Token { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTime StartTime { get; set; }
    public string? ExePath { get; set; }
}
