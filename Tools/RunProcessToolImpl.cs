using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace thuvu.Tools
{
    public class RunProcessToolImpl
    {
        // --- run_process (whitelisted) ---
        public static readonly HashSet<string> AllowedCmds = new(StringComparer.OrdinalIgnoreCase)
        { "dotnet", "git", "bash", "powershell" };

        public static Task<string> RunProcessToolAsync(string rawArgs) 
            => RunProcessToolAsync(rawArgs, CancellationToken.None);

        public static async Task<string> RunProcessToolAsync(string rawArgs, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(rawArgs);
            var cmd = doc.RootElement.GetProperty("cmd").GetString()!;
            if (!AllowedCmds.Contains(cmd))
                return JsonSerializer.Serialize(new { exit_code = -1, stdout = "", stderr = "command_not_allowed" });

            var args = new List<string>();
            if (doc.RootElement.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array)
                foreach (var it in a.EnumerateArray()) args.Add(it.GetString() ?? "");

            // Auto-wrap PowerShell/bash commands to ensure they execute and exit
            if (cmd.Equals("powershell", StringComparison.OrdinalIgnoreCase) && args.Count > 0 && !args[0].StartsWith("-"))
            {
                args.Insert(0, "-Command");
            }
            else if (cmd.Equals("bash", StringComparison.OrdinalIgnoreCase) && args.Count > 0 && !args[0].StartsWith("-"))
            {
                args.Insert(0, "-c");
                // Bash -c expects a single string command
                var bashCmd = string.Join(" ", args.Skip(1));
                args = new List<string> { "-c", bashCmd };
            }

            var workDir = thuvu.Models.AgentConfig.GetWorkDirectory();
            var cwd = doc.RootElement.TryGetProperty("cwd", out var cwdEl) ? cwdEl.GetString() : null;
            var timeoutMs = doc.RootElement.TryGetProperty("timeout_ms", out var tEl) ? Math.Clamp(tEl.GetInt32(), 1000, 600_000) : 120_000;

            var psi = new System.Diagnostics.ProcessStartInfo(cmd)
            {
                WorkingDirectory = string.IsNullOrWhiteSpace(cwd) ? workDir : cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var aStr in args) psi.ArgumentList.Add(aStr);

            var p = new System.Diagnostics.Process { StartInfo = psi };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            p.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            // Wait for process with cancellation support
            using var timeoutCts = new CancellationTokenSource(timeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            
            try
            {
                // Use WaitForExitAsync for proper async waiting (available in .NET 5+)
                await p.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
                return JsonSerializer.Serialize(new { exit_code = p.ExitCode, stdout = stdout.ToString(), stderr = stderr.ToString(), timed_out = false });
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                
                if (ct.IsCancellationRequested)
                {
                    return JsonSerializer.Serialize(new { exit_code = -1, stdout = stdout.ToString(), stderr = "cancelled", cancelled = true });
                }
                else
                {
                    return JsonSerializer.Serialize(new { exit_code = -1, stdout = stdout.ToString(), stderr = "timeout", timed_out = true });
                }
            }
        }

    }
}
