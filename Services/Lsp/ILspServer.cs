using System.Text.Json;

namespace thuvu.Services.Lsp;

/// <summary>
/// Abstraction for any Language Server Protocol server (OmniSharp, tsserver, pylsp, gopls, etc.)
/// </summary>
public interface ILspServer : IDisposable
{
    string ServerId { get; }
    string[] SupportedExtensions { get; }
    bool IsReady { get; }

    Task InitializeAsync(string projectRoot, CancellationToken ct = default);
    Task ShutdownAsync(CancellationToken ct = default);

    // Navigation
    Task<IReadOnlyList<LspLocation>> GoToDefinitionAsync(LspPosition position, CancellationToken ct = default);
    Task<IReadOnlyList<LspLocation>> FindReferencesAsync(LspPosition position, CancellationToken ct = default);
    Task<IReadOnlyList<LspLocation>> GoToImplementationAsync(LspPosition position, CancellationToken ct = default);
    Task<LspHoverResult?> HoverAsync(LspPosition position, CancellationToken ct = default);

    // Symbols
    Task<IReadOnlyList<LspDocumentSymbol>> DocumentSymbolAsync(string filePath, CancellationToken ct = default);
    Task<IReadOnlyList<LspSymbol>> WorkspaceSymbolAsync(string query, CancellationToken ct = default);

    // Call hierarchy
    Task<IReadOnlyList<LspCallHierarchyItem>> PrepareCallHierarchyAsync(LspPosition position, CancellationToken ct = default);
    Task<IReadOnlyList<LspCallHierarchyIncomingCall>> IncomingCallsAsync(LspCallHierarchyItem item, CancellationToken ct = default);
    Task<IReadOnlyList<LspCallHierarchyOutgoingCall>> OutgoingCallsAsync(LspCallHierarchyItem item, CancellationToken ct = default);

    // Diagnostics
    Task<IReadOnlyList<LspDiagnostic>> GetDiagnosticsAsync(string filePath, CancellationToken ct = default);
    IReadOnlyDictionary<string, IReadOnlyList<LspDiagnostic>> GetAllDiagnostics();

    // File sync
    Task NotifyFileOpenedAsync(string filePath, CancellationToken ct = default);
    Task NotifyFileChangedAsync(string filePath, CancellationToken ct = default);
}

// --- LSP data models ---

public record LspPosition(string FilePath, int Line, int Character);

public record LspRange(int StartLine, int StartCharacter, int EndLine, int EndCharacter);

public record LspLocation(string FilePath, LspRange Range);

public record LspHoverResult(string Contents, LspRange? Range);

public record LspDiagnostic(
    LspRange Range,
    LspDiagnosticSeverity Severity,
    string? Code,
    string? Source,
    string Message);

public enum LspDiagnosticSeverity
{
    Error = 1,
    Warning = 2,
    Information = 3,
    Hint = 4
}

public record LspDocumentSymbol(
    string Name,
    string? Detail,
    int Kind,
    LspRange Range,
    LspRange SelectionRange,
    IReadOnlyList<LspDocumentSymbol>? Children);

public record LspSymbol(string Name, int Kind, LspLocation Location);

public record LspCallHierarchyItem(
    string Name,
    int Kind,
    string Uri,
    LspRange Range,
    LspRange SelectionRange,
    string? Detail);

public record LspCallHierarchyIncomingCall(LspCallHierarchyItem From, IReadOnlyList<LspRange> FromRanges);

public record LspCallHierarchyOutgoingCall(LspCallHierarchyItem To, IReadOnlyList<LspRange> FromRanges);
