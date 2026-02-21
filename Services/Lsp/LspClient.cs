using System.Diagnostics;
using System.Text.Json;
using StreamJsonRpc;

namespace thuvu.Services.Lsp;

/// <summary>
/// JSON-RPC client wrapper for communicating with an LSP server over stdin/stdout.
/// </summary>
public class LspClient : IDisposable
{
    private readonly Process _process;
    private readonly JsonRpc _rpc;
    private bool _disposed;

    public event Action<string, JsonElement>? OnNotification;

    private LspClient(Process process, JsonRpc rpc)
    {
        _process = process;
        _rpc = rpc;
    }

    public static LspClient Start(string exePath, string[] args, string workingDirectory, Dictionary<string, string>? envVars = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        if (envVars != null)
            foreach (var (key, value) in envVars)
                psi.Environment[key] = value;

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start LSP server: {exePath}");

        // LSP uses Header-Delimited JSON-RPC (Content-Length headers)
        var handler = new HeaderDelimitedMessageHandler(
            process.StandardInput.BaseStream,
            process.StandardOutput.BaseStream);

        var rpc = new JsonRpc(handler);
        var client = new LspClient(process, rpc);

        // Capture notifications from the server
        rpc.AddLocalRpcMethod("$/progress", new Action<JsonElement>(data => { }));

        rpc.StartListening();
        return client;
    }

    public async Task<JsonElement> SendRequestAsync(string method, object? parameters = null, CancellationToken ct = default)
    {
        var result = await _rpc.InvokeWithParameterObjectAsync<JsonElement>(method, parameters, ct);
        return result;
    }

    public async Task SendNotificationAsync(string method, object? parameters = null)
    {
        await _rpc.NotifyWithParameterObjectAsync(method, parameters);
    }

    /// <summary>
    /// Register a handler for server-initiated notifications (e.g., textDocument/publishDiagnostics).
    /// </summary>
    public void RegisterNotificationHandler(string method, Action<JsonElement> handler)
    {
        _rpc.AddLocalRpcMethod(method, handler);
    }

    public bool IsAlive => !_process.HasExited;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (!_process.HasExited)
            {
                _rpc.Dispose();
                _process.Kill();
                _process.WaitForExit(3000);
            }
        }
        catch { }
        finally
        {
            _process.Dispose();
        }
    }
}
