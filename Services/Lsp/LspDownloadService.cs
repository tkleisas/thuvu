using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;

namespace thuvu.Services.Lsp;

/// <summary>
/// Auto-downloads OmniSharp binaries from GitHub releases.
/// </summary>
public static class LspDownloadService
{
    private static readonly string DefaultInstallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".thuvu", "lsp", "omnisharp");

    // Latest stable release tag — update periodically
    private const string OmniSharpVersion = "v1.39.12";
    private const string GitHubReleaseUrl = "https://github.com/OmniSharp/omnisharp-roslyn/releases/download";

    /// <summary>
    /// Ensure OmniSharp is available. Returns the path to OmniSharp.exe, or null if unavailable.
    /// Downloads automatically if AutoDownload is enabled and the binary is missing.
    /// </summary>
    public static async Task<string?> EnsureOmniSharpAsync(Models.LspServerConfig? config, CancellationToken ct = default)
    {
        // 1. Check explicit path in config
        if (!string.IsNullOrEmpty(config?.Path) && File.Exists(config.Path))
            return config.Path;

        // 2. Check default install location
        var defaultExe = Path.Combine(DefaultInstallDir, "OmniSharp.exe");
        if (File.Exists(defaultExe))
            return defaultExe;

        // 3. Check PATH
        var pathExe = FindInPath("OmniSharp.exe") ?? FindInPath("omnisharp");
        if (pathExe != null)
            return pathExe;

        // 4. Auto-download if enabled
        if (config?.AutoDownload != false)
        {
            return await DownloadOmniSharpAsync(DefaultInstallDir, ct);
        }

        return null;
    }

    private static async Task<string?> DownloadOmniSharpAsync(string installDir, CancellationToken ct)
    {
        var (assetName, exeName) = GetPlatformAsset();
        if (assetName == null)
        {
            ConsoleHelpers.PrintWarning($"[LSP] No OmniSharp binary available for {RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture}");
            return null;
        }

        var url = $"{GitHubReleaseUrl}/{OmniSharpVersion}/{assetName}";
        ConsoleHelpers.PrintInfo($"[LSP] Downloading OmniSharp {OmniSharpVersion}...");

        try
        {
            Directory.CreateDirectory(installDir);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

            var response = await http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var tempFile = Path.Combine(installDir, assetName);
            await using (var fs = File.Create(tempFile))
                await response.Content.CopyToAsync(fs, ct);

            // Extract
            if (assetName.EndsWith(".zip"))
            {
                ZipFile.ExtractToDirectory(tempFile, installDir, overwriteFiles: true);
                File.Delete(tempFile);
            }
            else if (assetName.EndsWith(".tar.gz"))
            {
                // Use tar command on Linux/macOS
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "tar",
                    ArgumentList = { "xzf", tempFile, "-C", installDir },
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                var proc = System.Diagnostics.Process.Start(psi);
                if (proc != null)
                {
                    await proc.WaitForExitAsync(ct);
                    File.Delete(tempFile);
                }
            }

            var exePath = Path.Combine(installDir, exeName);
            if (File.Exists(exePath))
            {
                ConsoleHelpers.PrintSuccess($"[LSP] OmniSharp installed to {installDir}");
                return exePath;
            }

            ConsoleHelpers.PrintWarning("[LSP] OmniSharp download succeeded but executable not found after extraction");
            return null;
        }
        catch (Exception ex)
        {
            ConsoleHelpers.PrintWarning($"[LSP] Failed to download OmniSharp: {ex.Message}");
            return null;
        }
    }

    private static (string? assetName, string exeName) GetPlatformAsset()
    {
        var arch = RuntimeInformation.ProcessArchitecture;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return arch switch
            {
                Architecture.X64 => ("omnisharp-win-x64-net6.0.zip", "OmniSharp.exe"),
                Architecture.Arm64 => ("omnisharp-win-arm64-net6.0.zip", "OmniSharp.exe"),
                _ => (null, "OmniSharp.exe")
            };
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return arch switch
            {
                Architecture.X64 => ("omnisharp-linux-x64-net6.0.tar.gz", "OmniSharp"),
                Architecture.Arm64 => ("omnisharp-linux-arm64-net6.0.tar.gz", "OmniSharp"),
                _ => (null, "OmniSharp")
            };
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return ("omnisharp-osx-net6.0.tar.gz", "OmniSharp");
        }

        return (null, "OmniSharp.exe");
    }

    private static string? FindInPath(string exeName)
    {
        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        foreach (var dir in pathDirs)
        {
            var p = Path.Combine(dir, exeName);
            if (File.Exists(p)) return p;
        }
        return null;
    }
}
