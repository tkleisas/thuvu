using System.Text.Json;
using thuvu.Services.Lsp;

namespace thuvu.Tools;

/// <summary>
/// LSP tool implementation exposing code intelligence operations to the agent.
/// </summary>
public static class LspToolImpl
{
    public static async Task<string> ExecuteAsync(string argsJson, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            var root = doc.RootElement;

            var operation = root.GetProperty("operation").GetString()
                ?? throw new ArgumentException("Missing 'operation'");
            var filePath = root.GetProperty("filePath").GetString()
                ?? throw new ArgumentException("Missing 'filePath'");

            // Resolve relative paths
            filePath = Path.GetFullPath(filePath, Models.AgentConfig.GetWorkDirectory());
            if (!File.Exists(filePath) && operation != "workspaceSymbol" && operation != "diagnostics")
                return JsonSerializer.Serialize(new { error = $"File not found: {filePath}" });

            var line = root.TryGetProperty("line", out var l) ? l.GetInt32() - 1 : 0; // Convert 1-based to 0-based
            var character = root.TryGetProperty("character", out var c) ? c.GetInt32() - 1 : 0;

            if (!LspService.Instance.IsInitialized)
                return JsonSerializer.Serialize(new { error = "LSP service not initialized" });

            var server = await LspService.Instance.GetServerForFileAsync(filePath, ct);
            if (server == null)
                return JsonSerializer.Serialize(new { error = $"No LSP server available for {Path.GetExtension(filePath)} files" });

            // Ensure file is opened in the server
            await server.NotifyFileOpenedAsync(filePath, ct);

            var position = new LspPosition(filePath, line, character);
            var relPath = Path.GetRelativePath(Models.AgentConfig.GetWorkDirectory(), filePath);
            var title = $"{operation} {relPath}:{line + 1}:{character + 1}";

            object result = operation switch
            {
                "goToDefinition" => await FormatLocations(LspService.Instance.GoToDefinitionAsync(position, ct)),
                "findReferences" => await FormatLocations(LspService.Instance.FindReferencesAsync(position, ct)),
                "goToImplementation" => await FormatLocations(LspService.Instance.GoToImplementationAsync(position, ct)),
                "hover" => await FormatHover(LspService.Instance.HoverAsync(position, ct)),
                "documentSymbol" => await FormatDocumentSymbols(LspService.Instance.DocumentSymbolAsync(filePath, ct)),
                "workspaceSymbol" => await FormatWorkspaceSymbols(root, ct),
                "prepareCallHierarchy" => await FormatCallHierarchy(LspService.Instance.PrepareCallHierarchyAsync(position, ct)),
                "incomingCalls" => await FormatIncomingCalls(LspService.Instance.IncomingCallsAsync(position, ct)),
                "outgoingCalls" => await FormatOutgoingCalls(LspService.Instance.OutgoingCallsAsync(position, ct)),
                "diagnostics" => await FormatDiagnostics(filePath, ct),
                _ => new { error = $"Unknown operation: {operation}" }
            };

            return JsonSerializer.Serialize(new { title, result });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    public static async Task<string> GetLspStatusAsync(CancellationToken ct)
    {
        try
        {
            if (!LspService.Instance.IsInitialized)
                return JsonSerializer.Serialize(new { status = "not_initialized" });

            var servers = LspService.Instance.GetStatus();
            return JsonSerializer.Serialize(new
            {
                status = "initialized",
                servers = servers.Select(s => new { s.ServerId, s.IsReady })
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    // --- Formatting helpers ---

    private static async Task<object> FormatLocations(Task<IReadOnlyList<LspLocation>> task)
    {
        var locations = await task;
        if (locations.Count == 0)
            return new { message = "No results found" };

        var workDir = Models.AgentConfig.GetWorkDirectory();
        return locations.Select(loc => new
        {
            file = Path.GetRelativePath(workDir, loc.FilePath),
            line = loc.Range.StartLine + 1,
            character = loc.Range.StartCharacter + 1,
            endLine = loc.Range.EndLine + 1,
            endCharacter = loc.Range.EndCharacter + 1
        });
    }

    private static async Task<object> FormatHover(Task<LspHoverResult?> task)
    {
        var hover = await task;
        if (hover == null)
            return new { message = "No hover information available" };
        return new { contents = hover.Contents };
    }

    private static async Task<object> FormatDocumentSymbols(Task<IReadOnlyList<LspDocumentSymbol>> task)
    {
        var symbols = await task;
        if (symbols.Count == 0)
            return new { message = "No symbols found" };

        return symbols.Select(FormatSymbol);
    }

    private static object FormatSymbol(LspDocumentSymbol s)
    {
        var result = new Dictionary<string, object?>
        {
            ["name"] = s.Name,
            ["kind"] = SymbolKindName(s.Kind),
            ["line"] = s.Range.StartLine + 1,
            ["endLine"] = s.Range.EndLine + 1
        };
        if (s.Detail != null) result["detail"] = s.Detail;
        if (s.Children?.Count > 0)
            result["children"] = s.Children.Select(FormatSymbol);
        return result;
    }

    private static async Task<object> FormatWorkspaceSymbols(JsonElement root, CancellationToken ct)
    {
        var query = root.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
        var symbols = await LspService.Instance.WorkspaceSymbolAsync(query, ct);
        if (symbols.Count == 0)
            return new { message = "No symbols found" };

        var workDir = Models.AgentConfig.GetWorkDirectory();
        return symbols.Select(s => new
        {
            name = s.Name,
            kind = SymbolKindName(s.Kind),
            file = Path.GetRelativePath(workDir, s.Location.FilePath),
            line = s.Location.Range.StartLine + 1
        });
    }

    private static async Task<object> FormatCallHierarchy(Task<IReadOnlyList<LspCallHierarchyItem>> task)
    {
        var items = await task;
        if (items.Count == 0)
            return new { message = "No call hierarchy items found" };

        return items.Select(i => new
        {
            name = i.Name,
            kind = SymbolKindName(i.Kind),
            detail = i.Detail,
            line = i.Range.StartLine + 1
        });
    }

    private static async Task<object> FormatIncomingCalls(Task<IReadOnlyList<LspCallHierarchyIncomingCall>> task)
    {
        var calls = await task;
        if (calls.Count == 0)
            return new { message = "No incoming calls found" };

        var workDir = Models.AgentConfig.GetWorkDirectory();
        return calls.Select(c => new
        {
            from = c.From.Name,
            kind = SymbolKindName(c.From.Kind),
            file = Path.GetRelativePath(workDir, new Uri(c.From.Uri).LocalPath),
            line = c.From.Range.StartLine + 1
        });
    }

    private static async Task<object> FormatOutgoingCalls(Task<IReadOnlyList<LspCallHierarchyOutgoingCall>> task)
    {
        var calls = await task;
        if (calls.Count == 0)
            return new { message = "No outgoing calls found" };

        var workDir = Models.AgentConfig.GetWorkDirectory();
        return calls.Select(c => new
        {
            to = c.To.Name,
            kind = SymbolKindName(c.To.Kind),
            file = Path.GetRelativePath(workDir, new Uri(c.To.Uri).LocalPath),
            line = c.To.Range.StartLine + 1
        });
    }

    private static async Task<object> FormatDiagnostics(string filePath, CancellationToken ct)
    {
        var diags = await LspService.Instance.GetDiagnosticsAsync(filePath, ct);
        if (diags.Count == 0)
            return new { message = "No diagnostics (clean)" };

        return diags.Select(d => new
        {
            severity = d.Severity.ToString().ToLower(),
            code = d.Code,
            message = d.Message,
            line = d.Range.StartLine + 1,
            character = d.Range.StartCharacter + 1
        });
    }

    private static string SymbolKindName(int kind) => kind switch
    {
        1 => "File", 2 => "Module", 3 => "Namespace", 4 => "Package",
        5 => "Class", 6 => "Method", 7 => "Property", 8 => "Field",
        9 => "Constructor", 10 => "Enum", 11 => "Interface", 12 => "Function",
        13 => "Variable", 14 => "Constant", 15 => "String", 16 => "Number",
        17 => "Boolean", 18 => "Array", 19 => "Object", 20 => "Key",
        21 => "Null", 22 => "EnumMember", 23 => "Struct", 24 => "Event",
        25 => "Operator", 26 => "TypeParameter",
        _ => $"Unknown({kind})"
    };
}
