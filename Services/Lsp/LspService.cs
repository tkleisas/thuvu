using System.Collections.Concurrent;

namespace thuvu.Services.Lsp;

/// <summary>
/// Singleton service that manages LSP server lifecycle and provides a unified facade.
/// Lazily spawns language servers based on file extension.
/// </summary>
public class LspService : IDisposable
{
    private static LspService? _instance;
    public static LspService Instance => _instance ?? throw new InvalidOperationException("LspService not initialized");

    private readonly ConcurrentDictionary<string, ILspServer> _servers = new();
    private readonly ConcurrentDictionary<string, string> _extensionToServerId = new();
    private readonly HashSet<string> _brokenServers = new();
    private readonly List<Func<string, ILspServer?>> _serverFactories = new();
    private string? _projectRoot;
    private bool _disposed;

    public bool IsInitialized => _projectRoot != null;
    public IReadOnlyDictionary<string, ILspServer> ActiveServers => _servers;

    public static LspService Initialize(string projectRoot)
    {
        _instance?.Dispose();
        _instance = new LspService { _projectRoot = projectRoot };
        return _instance;
    }

    /// <summary>
    /// Register a factory that creates an ILspServer for a given file extension.
    /// </summary>
    public void RegisterServerFactory(Func<string, ILspServer?> factory)
    {
        _serverFactories.Add(factory);
    }

    /// <summary>
    /// Get or lazily create the LSP server for the given file.
    /// </summary>
    public async Task<ILspServer?> GetServerForFileAsync(string filePath, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (string.IsNullOrEmpty(ext)) return null;

        // Check if we already have a server mapped for this extension
        if (_extensionToServerId.TryGetValue(ext, out var serverId) && _servers.TryGetValue(serverId, out var existing))
        {
            return existing.IsReady ? existing : null;
        }

        // Try to create a server via factories
        foreach (var factory in _serverFactories)
        {
            var server = factory(ext);
            if (server == null) continue;

            if (_brokenServers.Contains(server.ServerId))
                continue;

            if (_servers.TryGetValue(server.ServerId, out var alreadyRunning))
            {
                // Map this extension to the existing server
                foreach (var supportedExt in server.SupportedExtensions)
                    _extensionToServerId.TryAdd(supportedExt, server.ServerId);
                server.Dispose();
                return alreadyRunning.IsReady ? alreadyRunning : null;
            }

            try
            {
                await server.InitializeAsync(_projectRoot!, ct);
                _servers[server.ServerId] = server;
                foreach (var supportedExt in server.SupportedExtensions)
                    _extensionToServerId.TryAdd(supportedExt, server.ServerId);
                ConsoleHelpers.PrintInfo($"[LSP] {server.ServerId} started for {string.Join(", ", server.SupportedExtensions)}");
                return server;
            }
            catch (Exception ex)
            {
                _brokenServers.Add(server.ServerId);
                ConsoleHelpers.PrintWarning($"[LSP] Failed to start {server.ServerId}: {ex.Message}");
                server.Dispose();
            }
        }

        return null;
    }

    /// <summary>Check if any LSP server is available for the file type.</summary>
    public bool HasServerForFile(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (_extensionToServerId.TryGetValue(ext, out var sid) && _servers.ContainsKey(sid))
            return true;
        return _serverFactories.Any(f => { var s = f(ext); var ok = s != null; s?.Dispose(); return ok; });
    }

    // --- Facade methods (delegate to the appropriate server) ---

    public async Task<IReadOnlyList<LspLocation>> GoToDefinitionAsync(LspPosition position, CancellationToken ct = default)
    {
        var server = await GetServerForFileAsync(position.FilePath, ct);
        if (server == null) return Array.Empty<LspLocation>();
        return await server.GoToDefinitionAsync(position, ct);
    }

    public async Task<IReadOnlyList<LspLocation>> FindReferencesAsync(LspPosition position, CancellationToken ct = default)
    {
        var server = await GetServerForFileAsync(position.FilePath, ct);
        if (server == null) return Array.Empty<LspLocation>();
        return await server.FindReferencesAsync(position, ct);
    }

    public async Task<IReadOnlyList<LspLocation>> GoToImplementationAsync(LspPosition position, CancellationToken ct = default)
    {
        var server = await GetServerForFileAsync(position.FilePath, ct);
        if (server == null) return Array.Empty<LspLocation>();
        return await server.GoToImplementationAsync(position, ct);
    }

    public async Task<LspHoverResult?> HoverAsync(LspPosition position, CancellationToken ct = default)
    {
        var server = await GetServerForFileAsync(position.FilePath, ct);
        if (server == null) return null;
        return await server.HoverAsync(position, ct);
    }

    public async Task<IReadOnlyList<LspDocumentSymbol>> DocumentSymbolAsync(string filePath, CancellationToken ct = default)
    {
        var server = await GetServerForFileAsync(filePath, ct);
        if (server == null) return Array.Empty<LspDocumentSymbol>();
        return await server.DocumentSymbolAsync(filePath, ct);
    }

    public async Task<IReadOnlyList<LspSymbol>> WorkspaceSymbolAsync(string query, CancellationToken ct = default)
    {
        var results = new List<LspSymbol>();
        foreach (var server in _servers.Values.Where(s => s.IsReady))
        {
            var symbols = await server.WorkspaceSymbolAsync(query, ct);
            results.AddRange(symbols);
        }
        return results;
    }

    public async Task<IReadOnlyList<LspCallHierarchyItem>> PrepareCallHierarchyAsync(LspPosition position, CancellationToken ct = default)
    {
        var server = await GetServerForFileAsync(position.FilePath, ct);
        if (server == null) return Array.Empty<LspCallHierarchyItem>();
        return await server.PrepareCallHierarchyAsync(position, ct);
    }

    public async Task<IReadOnlyList<LspCallHierarchyIncomingCall>> IncomingCallsAsync(LspPosition position, CancellationToken ct = default)
    {
        var server = await GetServerForFileAsync(position.FilePath, ct);
        if (server == null) return Array.Empty<LspCallHierarchyIncomingCall>();
        var items = await server.PrepareCallHierarchyAsync(position, ct);
        if (items.Count == 0) return Array.Empty<LspCallHierarchyIncomingCall>();
        return await server.IncomingCallsAsync(items[0], ct);
    }

    public async Task<IReadOnlyList<LspCallHierarchyOutgoingCall>> OutgoingCallsAsync(LspPosition position, CancellationToken ct = default)
    {
        var server = await GetServerForFileAsync(position.FilePath, ct);
        if (server == null) return Array.Empty<LspCallHierarchyOutgoingCall>();
        var items = await server.PrepareCallHierarchyAsync(position, ct);
        if (items.Count == 0) return Array.Empty<LspCallHierarchyOutgoingCall>();
        return await server.OutgoingCallsAsync(items[0], ct);
    }

    public async Task<IReadOnlyList<LspDiagnostic>> GetDiagnosticsAsync(string filePath, CancellationToken ct = default)
    {
        var server = await GetServerForFileAsync(filePath, ct);
        if (server == null) return Array.Empty<LspDiagnostic>();
        return await server.GetDiagnosticsAsync(filePath, ct);
    }

    public async Task NotifyFileChangedAsync(string filePath, CancellationToken ct = default)
    {
        var server = await GetServerForFileAsync(filePath, ct);
        if (server != null)
            await server.NotifyFileChangedAsync(filePath, ct);
    }

    /// <summary>Get diagnostics summary for display after file writes.</summary>
    public async Task<string?> GetDiagnosticsSummaryAsync(string filePath, CancellationToken ct = default)
    {
        var diagnostics = await GetDiagnosticsAsync(filePath, ct);
        var errors = diagnostics.Where(d => d.Severity == LspDiagnosticSeverity.Error).ToList();
        var warnings = diagnostics.Where(d => d.Severity == LspDiagnosticSeverity.Warning).ToList();

        if (errors.Count == 0 && warnings.Count == 0)
            return null;

        var parts = new List<string>();
        if (errors.Count > 0)
        {
            parts.Add($"{errors.Count} error(s):");
            foreach (var e in errors.Take(5))
                parts.Add($"  Line {e.Range.StartLine + 1}: {e.Code ?? ""} {e.Message}");
            if (errors.Count > 5) parts.Add($"  ... and {errors.Count - 5} more");
        }
        if (warnings.Count > 0)
        {
            parts.Add($"{warnings.Count} warning(s):");
            foreach (var w in warnings.Take(3))
                parts.Add($"  Line {w.Range.StartLine + 1}: {w.Code ?? ""} {w.Message}");
            if (warnings.Count > 3) parts.Add($"  ... and {warnings.Count - 3} more");
        }
        return string.Join("\n", parts);
    }

    /// <summary>Mark a broken server as retryable.</summary>
    public void ResetBrokenServer(string serverId)
    {
        _brokenServers.Remove(serverId);
    }

    public IReadOnlyList<(string ServerId, bool IsReady)> GetStatus()
    {
        return _servers.Values.Select(s => (s.ServerId, s.IsReady)).ToList();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var server in _servers.Values)
        {
            try { server.Dispose(); }
            catch { }
        }
        _servers.Clear();
        _extensionToServerId.Clear();
    }
}
