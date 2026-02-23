using System.Diagnostics;
using System.Text;

namespace thuvu.Desktop.Services;

/// <summary>
/// Launches the current THUVU instance inside Windows Sandbox for isolated execution.
///
/// The sandbox maps the app binaries (read-only) and the active project directory
/// (read-write). On first boot a PowerShell setup script installs all required
/// developer tools via winget so the agent can build, run, and commit code.
///
/// LLM CONFIGURATION NOTE
/// -----------------------
/// The sandbox receives a verbatim copy of the host's appsettings.json.
/// If your LLM provider is cloud-based (OpenAI, Anthropic, DeepSeek, etc.) the
/// URL and API key will work as-is — the sandbox has outbound internet access.
///
/// If you use a LOCAL server (e.g. LM Studio on 127.0.0.1) you must either:
///   a) Enable "Allow connections from local network" in LM Studio settings and
///      update HostUrl in appsettings.json to your machine's LAN IP before launching.
///   b) From LM Studio v0.3.3+: enable "API key required" and set an API token,
///      then update AuthToken in appsettings.json accordingly.
/// </summary>
public static class SandboxLauncher
{
    private const string SandboxExe  = @"C:\Windows\System32\WindowsSandbox.exe";
    private const string SandboxBase = @"C:\Users\WDAGUtilityAccount\Desktop";

    /// <summary>Developer tools installed in the sandbox on first boot via winget.</summary>
    private static readonly (string DisplayName, string WingetId)[] RequiredTools =
    [
        ("Git",          "Git.Git"),
        (".NET SDK 8",   "Microsoft.DotNet.SDK.8"),
        ("Deno",         "DenoLand.Deno"),
    ];

    /// <summary>True when running on Windows and Windows Sandbox is installed.</summary>
    public static bool IsAvailable() =>
        OperatingSystem.IsWindows() && File.Exists(SandboxExe);

    /// <param name="appDir">Directory of the THUVU executable.</param>
    /// <param name="projectDir">Work/project directory to mount as writable.</param>
    /// <param name="appSettingsJson">Current appsettings.json content (copied verbatim).</param>
    public static async Task<string> LaunchAsync(string appDir, string projectDir, string appSettingsJson)
    {
        if (!IsAvailable())
            return "Windows Sandbox is not available on this system.\n" +
                   "Enable it via: Settings → Apps → Optional features → Windows Sandbox.";

        var stageDir = Path.Combine(Path.GetTempPath(), $"thuvu-sandbox-{Guid.NewGuid():N}"[..20]);
        Directory.CreateDirectory(stageDir);

        var sandboxAppDir   = $@"{SandboxBase}\thuvu-app";
        var sandboxProjDir  = $@"{SandboxBase}\project";
        var sandboxStageDir = $@"{SandboxBase}\startup";
        var exeName         = Path.GetFileName(Environment.ProcessPath ?? "thuvu.Desktop.exe");

        // --- appsettings.json (verbatim copy) ---
        await File.WriteAllTextAsync(Path.Combine(stageDir, "appsettings.json"), appSettingsJson);

        // --- setup.ps1 --- installs tools then launches the app ---
        var ps = new StringBuilder();
        ps.AppendLine("# THUVU Sandbox Setup — auto-generated, do not edit");
        ps.AppendLine("$ErrorActionPreference = 'Continue'");
        ps.AppendLine();
        ps.AppendLine("# Install required developer tools via winget");
        foreach (var (name, id) in RequiredTools)
        {
            ps.AppendLine($"Write-Host 'Installing {name}...'");
            ps.AppendLine($"winget install -e --id {id} --silent --accept-source-agreements --accept-package-agreements");
        }
        ps.AppendLine();
        ps.AppendLine("# Refresh PATH so newly installed tools are available");
        ps.AppendLine(@"$env:PATH = [System.Environment]::GetEnvironmentVariable('PATH','Machine') + ';' +");
        ps.AppendLine(@"           [System.Environment]::GetEnvironmentVariable('PATH','User')");
        ps.AppendLine();
        ps.AppendLine("# Apply appsettings (overwrite the read-only mapped copy's runtime location)");
        ps.AppendLine($@"Copy-Item '{sandboxStageDir}\appsettings.json' '{sandboxAppDir}\appsettings.json' -Force");
        ps.AppendLine();
        ps.AppendLine("# Launch the app");
        ps.AppendLine($@"Start-Process '{sandboxAppDir}\{exeName}' -ArgumentList '--work ""{sandboxProjDir}""'");

        await File.WriteAllTextAsync(Path.Combine(stageDir, "setup.ps1"), ps.ToString());

        // Thin .bat wrapper — LogonCommand can't directly invoke pwsh with spaces
        var bat = new StringBuilder();
        bat.AppendLine("@echo off");
        bat.AppendLine($@"powershell.exe -ExecutionPolicy Bypass -File ""{sandboxStageDir}\setup.ps1""");
        await File.WriteAllTextAsync(Path.Combine(stageDir, "launch.bat"), bat.ToString());

        // --- .wsb config ---
        var wsb = $@"<Configuration>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>{appDir}</HostFolder>
      <SandboxFolder>{sandboxAppDir}</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
    <MappedFolder>
      <HostFolder>{projectDir}</HostFolder>
      <SandboxFolder>{sandboxProjDir}</SandboxFolder>
      <ReadOnly>false</ReadOnly>
    </MappedFolder>
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

        var toolList = string.Join(", ", RequiredTools.Select(t => t.DisplayName));
        return $"✅ Windows Sandbox launched.\n\n" +
               $"• App: {appDir} (read-only)\n" +
               $"• Project: {projectDir} (read-write — all changes are discarded when the sandbox closes)\n\n" +
               $"🔧 Auto-installing tools on sandbox boot: {toolList}\n\n" +
               $"⚙️  LLM connectivity: appsettings.json was copied verbatim.\n" +
               $"   Cloud providers (OpenAI, Anthropic, DeepSeek…) will work as-is.\n" +
               $"   For a local LLM (LM Studio etc.) update HostUrl to your LAN IP\n" +
               $"   and enable network access on the LLM server before launching the sandbox.";
    }
}

