using System.Collections.Concurrent;
using System.Text.Json;

namespace thuvu.Services.Lsp;

/// <summary>
/// OmniSharp LSP server implementation for C# code intelligence.
/// Communicates with OmniSharp.exe via stdio JSON-RPC.
/// </summary>
public class OmniSharpServer : ILspServer
{
    private LspClient? _client;
    private string? _projectRoot;
    private readonly ConcurrentDictionary<string, List<LspDiagnostic>> _diagnostics = new();
    private readonly ConcurrentDictionary<string, int> _fileVersions = new();
    private readonly SemaphoreSlim _diagSemaphore = new(0, int.MaxValue);
    private volatile bool _ready;

    public string ServerId => "omnisharp";
    public string[] SupportedExtensions => new[] { ".cs", ".csx" };
    public bool IsReady => _ready && _client?.IsAlive == true;

    public string? ExePath { get; set; }

    public async Task InitializeAsync(string projectRoot, CancellationToken ct = default)
    {
        _projectRoot = projectRoot;

        var exePath = ExePath ?? FindOmniSharp();
        if (exePath == null)
            throw new FileNotFoundException("OmniSharp not found. Set LspConfig.Servers.omnisharp.Path or install OmniSharp.");

        _client = LspClient.Start(exePath, new[] { "--stdio" }, projectRoot);

        // Register diagnostics handler
        _client.RegisterNotificationHandler("textDocument/publishDiagnostics", OnPublishDiagnostics);

        // Send initialize request
        var initResult = await _client.SendRequestAsync("initialize", new
        {
            processId = System.Diagnostics.Process.GetCurrentProcess().Id,
            rootUri = new Uri(projectRoot).AbsoluteUri,
            workspaceFolders = new[]
            {
                new { name = "workspace", uri = new Uri(projectRoot).AbsoluteUri }
            },
            capabilities = new
            {
                textDocument = new
                {
                    synchronization = new { didOpen = true, didChange = true, didSave = true },
                    publishDiagnostics = new { versionSupport = true },
                    definition = new { dynamicRegistration = false },
                    references = new { dynamicRegistration = false },
                    hover = new { contentFormat = new[] { "markdown", "plaintext" } },
                    documentSymbol = new { dynamicRegistration = false },
                    implementation = new { dynamicRegistration = false },
                    callHierarchy = new { dynamicRegistration = false }
                },
                workspace = new
                {
                    configuration = true,
                    symbol = new { dynamicRegistration = false },
                    workspaceFolders = true
                }
            }
        }, ct);

        await _client.SendNotificationAsync("initialized");
        _ready = true;
    }

    public async Task ShutdownAsync(CancellationToken ct = default)
    {
        if (_client == null) return;
        _ready = false;
        try
        {
            await _client.SendRequestAsync("shutdown", ct: ct);
            await _client.SendNotificationAsync("exit");
        }
        catch { }
    }

    // --- Navigation ---

    public async Task<IReadOnlyList<LspLocation>> GoToDefinitionAsync(LspPosition position, CancellationToken ct = default)
    {
        EnsureReady();
        var result = await _client!.SendRequestAsync("textDocument/definition",
            MakeTextDocumentPositionParams(position), ct);
        return ParseLocations(result);
    }

    public async Task<IReadOnlyList<LspLocation>> FindReferencesAsync(LspPosition position, CancellationToken ct = default)
    {
        EnsureReady();
        var result = await _client!.SendRequestAsync("textDocument/references", new
        {
            textDocument = new { uri = FileUri(position.FilePath) },
            position = new { line = position.Line, character = position.Character },
            context = new { includeDeclaration = true }
        }, ct);
        return ParseLocations(result);
    }

    public async Task<IReadOnlyList<LspLocation>> GoToImplementationAsync(LspPosition position, CancellationToken ct = default)
    {
        EnsureReady();
        var result = await _client!.SendRequestAsync("textDocument/implementation",
            MakeTextDocumentPositionParams(position), ct);
        return ParseLocations(result);
    }

    public async Task<LspHoverResult?> HoverAsync(LspPosition position, CancellationToken ct = default)
    {
        EnsureReady();
        var result = await _client!.SendRequestAsync("textDocument/hover",
            MakeTextDocumentPositionParams(position), ct);

        if (result.ValueKind == JsonValueKind.Null) return null;

        var contents = "";
        if (result.TryGetProperty("contents", out var c))
        {
            if (c.ValueKind == JsonValueKind.String)
                contents = c.GetString() ?? "";
            else if (c.TryGetProperty("value", out var v))
                contents = v.GetString() ?? "";
            else if (c.ValueKind == JsonValueKind.Array)
                contents = string.Join("\n", c.EnumerateArray().Select(ExtractMarkupContent));
        }

        LspRange? range = null;
        if (result.TryGetProperty("range", out var r))
            range = ParseRange(r);

        return new LspHoverResult(contents, range);
    }

    // --- Symbols ---

    public async Task<IReadOnlyList<LspDocumentSymbol>> DocumentSymbolAsync(string filePath, CancellationToken ct = default)
    {
        EnsureReady();
        var result = await _client!.SendRequestAsync("textDocument/documentSymbol", new
        {
            textDocument = new { uri = FileUri(filePath) }
        }, ct);

        if (result.ValueKind != JsonValueKind.Array) return Array.Empty<LspDocumentSymbol>();
        return result.EnumerateArray().Select(ParseDocumentSymbol).ToList();
    }

    public async Task<IReadOnlyList<LspSymbol>> WorkspaceSymbolAsync(string query, CancellationToken ct = default)
    {
        EnsureReady();
        var result = await _client!.SendRequestAsync("workspace/symbol", new { query }, ct);
        if (result.ValueKind != JsonValueKind.Array) return Array.Empty<LspSymbol>();

        return result.EnumerateArray()
            .Select(e =>
            {
                var name = e.GetProperty("name").GetString() ?? "";
                var kind = e.GetProperty("kind").GetInt32();
                var loc = ParseLocation(e.GetProperty("location"));
                return new LspSymbol(name, kind, loc);
            })
            .ToList();
    }

    // --- Call Hierarchy ---

    public async Task<IReadOnlyList<LspCallHierarchyItem>> PrepareCallHierarchyAsync(LspPosition position, CancellationToken ct = default)
    {
        EnsureReady();
        var result = await _client!.SendRequestAsync("textDocument/prepareCallHierarchy",
            MakeTextDocumentPositionParams(position), ct);
        if (result.ValueKind != JsonValueKind.Array) return Array.Empty<LspCallHierarchyItem>();
        return result.EnumerateArray().Select(ParseCallHierarchyItem).ToList();
    }

    public async Task<IReadOnlyList<LspCallHierarchyIncomingCall>> IncomingCallsAsync(LspCallHierarchyItem item, CancellationToken ct = default)
    {
        EnsureReady();
        var result = await _client!.SendRequestAsync("callHierarchy/incomingCalls", new
        {
            item = SerializeCallHierarchyItem(item)
        }, ct);
        if (result.ValueKind != JsonValueKind.Array) return Array.Empty<LspCallHierarchyIncomingCall>();
        return result.EnumerateArray().Select(e =>
        {
            var from = ParseCallHierarchyItem(e.GetProperty("from"));
            var ranges = e.GetProperty("fromRanges").EnumerateArray().Select(ParseRange).ToList();
            return new LspCallHierarchyIncomingCall(from, ranges);
        }).ToList();
    }

    public async Task<IReadOnlyList<LspCallHierarchyOutgoingCall>> OutgoingCallsAsync(LspCallHierarchyItem item, CancellationToken ct = default)
    {
        EnsureReady();
        var result = await _client!.SendRequestAsync("callHierarchy/outgoingCalls", new
        {
            item = SerializeCallHierarchyItem(item)
        }, ct);
        if (result.ValueKind != JsonValueKind.Array) return Array.Empty<LspCallHierarchyOutgoingCall>();
        return result.EnumerateArray().Select(e =>
        {
            var to = ParseCallHierarchyItem(e.GetProperty("to"));
            var ranges = e.GetProperty("fromRanges").EnumerateArray().Select(ParseRange).ToList();
            return new LspCallHierarchyOutgoingCall(to, ranges);
        }).ToList();
    }

    // --- Diagnostics ---

    public Task<IReadOnlyList<LspDiagnostic>> GetDiagnosticsAsync(string filePath, CancellationToken ct = default)
    {
        var normalized = Path.GetFullPath(filePath);
        if (_diagnostics.TryGetValue(normalized, out var diags))
            return Task.FromResult<IReadOnlyList<LspDiagnostic>>(diags.ToList());
        return Task.FromResult<IReadOnlyList<LspDiagnostic>>(Array.Empty<LspDiagnostic>());
    }

    public IReadOnlyDictionary<string, IReadOnlyList<LspDiagnostic>> GetAllDiagnostics()
    {
        return _diagnostics.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<LspDiagnostic>)kvp.Value.ToList());
    }

    // --- File Sync ---

    public async Task NotifyFileOpenedAsync(string filePath, CancellationToken ct = default)
    {
        EnsureReady();
        var text = await File.ReadAllTextAsync(filePath, ct);
        var uri = FileUri(filePath);
        _fileVersions[filePath] = 0;

        await _client!.SendNotificationAsync("textDocument/didOpen", new
        {
            textDocument = new
            {
                uri,
                languageId = "csharp",
                version = 0,
                text
            }
        });
    }

    public async Task NotifyFileChangedAsync(string filePath, CancellationToken ct = default)
    {
        EnsureReady();
        var text = await File.ReadAllTextAsync(filePath, ct);
        var uri = FileUri(filePath);

        if (_fileVersions.TryGetValue(filePath, out var version))
        {
            var next = version + 1;
            _fileVersions[filePath] = next;

            await _client!.SendNotificationAsync("textDocument/didChange", new
            {
                textDocument = new { uri, version = next },
                contentChanges = new[] { new { text } }
            });
        }
        else
        {
            // First time seeing this file — open it
            await NotifyFileOpenedAsync(filePath, ct);
        }
    }

    /// <summary>
    /// Notify the server of a file change and wait for diagnostics to arrive.
    /// </summary>
    public async Task<IReadOnlyList<LspDiagnostic>> NotifyAndWaitForDiagnosticsAsync(string filePath, int timeoutMs = 3000, CancellationToken ct = default)
    {
        await NotifyFileChangedAsync(filePath, ct);

        // Wait for diagnostics with a timeout
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            await _diagSemaphore.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException) { }

        return await GetDiagnosticsAsync(filePath, ct);
    }

    // --- Private helpers ---

    private void EnsureReady()
    {
        if (!IsReady) throw new InvalidOperationException("OmniSharp server is not ready");
    }

    private void OnPublishDiagnostics(JsonElement data)
    {
        try
        {
            var uri = data.GetProperty("uri").GetString() ?? "";
            var filePath = Path.GetFullPath(new Uri(uri).LocalPath);
            var diags = new List<LspDiagnostic>();

            if (data.TryGetProperty("diagnostics", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in arr.EnumerateArray())
                {
                    var range = ParseRange(d.GetProperty("range"));
                    var severity = d.TryGetProperty("severity", out var s) ? (LspDiagnosticSeverity)s.GetInt32() : LspDiagnosticSeverity.Error;
                    var code = d.TryGetProperty("code", out var c) ? c.ToString() : null;
                    var source = d.TryGetProperty("source", out var src) ? src.GetString() : null;
                    var message = d.GetProperty("message").GetString() ?? "";
                    diags.Add(new LspDiagnostic(range, severity, code, source, message));
                }
            }

            _diagnostics[filePath] = diags;
            _diagSemaphore.Release();
        }
        catch { }
    }

    private static object MakeTextDocumentPositionParams(LspPosition position) => new
    {
        textDocument = new { uri = FileUri(position.FilePath) },
        position = new { line = position.Line, character = position.Character }
    };

    private static string FileUri(string filePath) => new Uri(Path.GetFullPath(filePath)).AbsoluteUri;

    private static IReadOnlyList<LspLocation> ParseLocations(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null) return Array.Empty<LspLocation>();
        if (element.ValueKind == JsonValueKind.Object)
            return new[] { ParseLocation(element) };
        if (element.ValueKind == JsonValueKind.Array)
            return element.EnumerateArray().Select(ParseLocation).ToList();
        return Array.Empty<LspLocation>();
    }

    private static LspLocation ParseLocation(JsonElement e)
    {
        var uri = e.GetProperty("uri").GetString() ?? "";
        var filePath = new Uri(uri).LocalPath;
        var range = ParseRange(e.GetProperty("range"));
        return new LspLocation(filePath, range);
    }

    private static LspRange ParseRange(JsonElement e)
    {
        var start = e.GetProperty("start");
        var end = e.GetProperty("end");
        return new LspRange(
            start.GetProperty("line").GetInt32(),
            start.GetProperty("character").GetInt32(),
            end.GetProperty("line").GetInt32(),
            end.GetProperty("character").GetInt32());
    }

    private static LspDocumentSymbol ParseDocumentSymbol(JsonElement e)
    {
        var name = e.GetProperty("name").GetString() ?? "";
        var detail = e.TryGetProperty("detail", out var d) ? d.GetString() : null;
        var kind = e.GetProperty("kind").GetInt32();
        var range = ParseRange(e.GetProperty("range"));
        var selRange = ParseRange(e.GetProperty("selectionRange"));
        List<LspDocumentSymbol>? children = null;
        if (e.TryGetProperty("children", out var ch) && ch.ValueKind == JsonValueKind.Array)
            children = ch.EnumerateArray().Select(ParseDocumentSymbol).ToList();
        return new LspDocumentSymbol(name, detail, kind, range, selRange, children);
    }

    private static LspCallHierarchyItem ParseCallHierarchyItem(JsonElement e)
    {
        return new LspCallHierarchyItem(
            e.GetProperty("name").GetString() ?? "",
            e.GetProperty("kind").GetInt32(),
            e.GetProperty("uri").GetString() ?? "",
            ParseRange(e.GetProperty("range")),
            ParseRange(e.GetProperty("selectionRange")),
            e.TryGetProperty("detail", out var d) ? d.GetString() : null);
    }

    private static object SerializeCallHierarchyItem(LspCallHierarchyItem item) => new
    {
        name = item.Name,
        kind = item.Kind,
        uri = item.Uri,
        range = new
        {
            start = new { line = item.Range.StartLine, character = item.Range.StartCharacter },
            end = new { line = item.Range.EndLine, character = item.Range.EndCharacter }
        },
        selectionRange = new
        {
            start = new { line = item.SelectionRange.StartLine, character = item.SelectionRange.StartCharacter },
            end = new { line = item.SelectionRange.EndLine, character = item.SelectionRange.EndCharacter }
        },
        detail = item.Detail
    };

    private static string ExtractMarkupContent(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.String) return e.GetString() ?? "";
        if (e.TryGetProperty("value", out var v)) return v.GetString() ?? "";
        return e.ToString();
    }

    private static string? FindOmniSharp()
    {
        // Check well-known locations
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".thuvu", "lsp", "omnisharp", "OmniSharp.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".omnisharp", "OmniSharp.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "omnisharp-roslyn", "OmniSharp.exe"),
        };

        foreach (var candidate in candidates)
            if (File.Exists(candidate))
                return candidate;

        // Check PATH
        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        foreach (var dir in pathDirs)
        {
            var p = Path.Combine(dir, "OmniSharp.exe");
            if (File.Exists(p)) return p;
        }

        return null;
    }

    public void Dispose()
    {
        _ready = false;
        try
        {
            if (_client?.IsAlive == true)
            {
                _client.SendRequestAsync("shutdown").GetAwaiter().GetResult();
                _client.SendNotificationAsync("exit").GetAwaiter().GetResult();
            }
        }
        catch { }
        _client?.Dispose();
        _diagSemaphore.Dispose();
    }
}
