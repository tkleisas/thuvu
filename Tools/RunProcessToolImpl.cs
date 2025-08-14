using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace CodingAgent.Tools
{
    public class RunProcessToolImpl
    {
        // --- run_process (whitelisted) ---
        private static readonly HashSet<string> AllowedCmds = new(StringComparer.OrdinalIgnoreCase)
{ "dotnet", "bash", "powershell" };

        public static async Task<string> RunProcessToolAsync(string rawArgs)
        {
            using var doc = JsonDocument.Parse(rawArgs);
            var cmd = doc.RootElement.GetProperty("cmd").GetString()!;
            if (!AllowedCmds.Contains(cmd))
                return JsonSerializer.Serialize(new { exit_code = -1, stdout = "", stderr = "command_not_allowed" });

            var args = new List<string>();
            if (doc.RootElement.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array)
                foreach (var it in a.EnumerateArray()) args.Add(it.GetString() ?? "");

            var cwd = doc.RootElement.TryGetProperty("cwd", out var cwdEl) ? cwdEl.GetString() : null;
            var timeoutMs = doc.RootElement.TryGetProperty("timeout_ms", out var tEl) ? Math.Clamp(tEl.GetInt32(), 1000, 600_000) : 120_000;

            var psi = new System.Diagnostics.ProcessStartInfo(cmd)
            {
                WorkingDirectory = string.IsNullOrWhiteSpace(cwd) ? Directory.GetCurrentDirectory() : cwd,
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

            var exited = await Task.Run(() => p.WaitForExit(timeoutMs));
            if (!exited)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return JsonSerializer.Serialize(new { exit_code = -1, stdout = stdout.ToString(), stderr = "timeout", timed_out = true });
            }

            return JsonSerializer.Serialize(new { exit_code = p.ExitCode, stdout = stdout.ToString(), stderr = stderr.ToString(), timed_out = false });
        }

    }
}
