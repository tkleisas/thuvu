using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace thuvu.Desktop.Services;

/// <summary>
/// Launches the current THUVU instance inside Windows Sandbox for isolated execution.
/// The sandbox maps the app (read-only) and the active project directory (read-write),
/// then discards all changes when closed.
/// </summary>
public static class SandboxLauncher
{
    private const string SandboxExe = @"C:\Windows\System32\WindowsSandbox.exe";
    private const string SandboxBase = @"C:\Users\WDAGUtilityAccount\Desktop";

    /// <summary>True when running on Windows and Windows Sandbox is installed.</summary>
    public static bool IsAvailable() =>
        OperatingSystem.IsWindows() && File.Exists(SandboxExe);

    /// <summary>
    /// Generates a .wsb configuration file and launches Windows Sandbox with:
    ///   - App directory mapped read-only
    ///   - Project/work directory mapped read-write (changes are discardable)
    ///   - A startup script that patches appsettings.json so the LLM host URL
    ///     points to the host machine's IP (sandbox can't use 127.0.0.1).
    /// </summary>
    /// <param name="appDir">Directory of the THUVU executable (e.g. bin/Debug/net8.0).</param>
    /// <param name="projectDir">The work/project directory to mount as writable.</param>
    /// <param name="appSettingsJson">Current appsettings.json content to patch.</param>
    /// <returns>A status message describing what was launched.</returns>
    public static async Task<string> LaunchAsync(string appDir, string projectDir, string appSettingsJson)
    {
        if (!IsAvailable())
            return "Windows Sandbox is not available on this system. " +
                   "Enable it via: Settings → Apps → Optional features → Windows Sandbox.";

        // Get the host machine's LAN IP — 127.0.0.1 is the sandbox's own loopback,
        // not the host. We substitute it so the LLM server on the host is reachable.
        var hostIp = GetHostNetworkIp();

        // Patch the host URL in appsettings so it points to the host machine
        var patchedSettings = PatchHostUrls(appSettingsJson, hostIp);

        // Create a temp staging directory for the startup script and patched config
        var stageDir = Path.Combine(Path.GetTempPath(), $"thuvu-sandbox-{Guid.NewGuid():N}"[..20]);
        Directory.CreateDirectory(stageDir);

        // Write the patched appsettings
        await File.WriteAllTextAsync(Path.Combine(stageDir, "appsettings.json"), patchedSettings);

        // Write the startup .bat that copies the patched config and launches the app
        var sandboxAppDir   = $@"{SandboxBase}\thuvu-app";
        var sandboxProjDir  = $@"{SandboxBase}\project";
        var sandboxStageDir = $@"{SandboxBase}\startup";
        var sandboxExeName  = Path.GetFileName(Environment.ProcessPath ?? "thuvu.Desktop.exe");

        var launchScript = new StringBuilder();
        launchScript.AppendLine("@echo off");
        launchScript.AppendLine($@"copy /Y ""{sandboxStageDir}\appsettings.json"" ""{sandboxAppDir}\appsettings.json""");
        launchScript.AppendLine($@"start """" ""{sandboxAppDir}\{sandboxExeName}"" --work ""{sandboxProjDir}""");
        await File.WriteAllTextAsync(Path.Combine(stageDir, "launch.bat"), launchScript.ToString());

        // Build .wsb XML
        var wsb = $@"<Configuration>
  <!-- App binaries (read-only — not modified by the agent) -->
  <MappedFolders>
    <MappedFolder>
      <HostFolder>{appDir}</HostFolder>
      <SandboxFolder>{sandboxAppDir}</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
    <!-- Project directory (read-write — discard on sandbox close) -->
    <MappedFolder>
      <HostFolder>{projectDir}</HostFolder>
      <SandboxFolder>{sandboxProjDir}</SandboxFolder>
      <ReadOnly>false</ReadOnly>
    </MappedFolder>
    <!-- Startup scripts and patched appsettings -->
    <MappedFolder>
      <HostFolder>{stageDir}</HostFolder>
      <SandboxFolder>{sandboxStageDir}</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
  </MappedFolders>
  <LogonCommand>
    <Command>{sandboxStageDir}\launch.bat</Command>
  </LogonCommand>
  <Networking>Default</Networking>
  <vGPU>Default</vGPU>
</Configuration>";

        var wsbPath = Path.Combine(stageDir, "thuvu.wsb");
        await File.WriteAllTextAsync(wsbPath, wsb);

        Process.Start(new ProcessStartInfo { FileName = wsbPath, UseShellExecute = true });

        return $"Windows Sandbox launched.\n" +
               $"• App: {appDir} (read-only)\n" +
               $"• Project: {projectDir} (read-write, discarded on close)\n" +
               $"• LLM host URL patched: localhost → {hostIp}\n\n" +
               $"Note: .NET 8 runtime must be available in the sandbox. " +
               $"If the app doesn't start, publish it as self-contained first.";
    }

    /// <summary>Returns the host machine's first non-loopback IPv4 address.</summary>
    private static string GetHostNetworkIp()
    {
        try
        {
            var addresses = Dns.GetHostEntry(Dns.GetHostName()).AddressList;
            var ip = addresses.FirstOrDefault(a =>
                a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
            return ip?.ToString() ?? "127.0.0.1";
        }
        catch
        {
            return "127.0.0.1";
        }
    }

    /// <summary>Replaces loopback addresses in the JSON with the given host IP.</summary>
    private static string PatchHostUrls(string json, string hostIp) =>
        Regex.Replace(json, @"(?<=://)(?:localhost|127\.0\.0\.1)", hostIp);
}
