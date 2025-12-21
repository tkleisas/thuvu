using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace thuvu.Tools
{
    public class DotnetToolImpl
    {
        // --- dotnet wrappers (build/test/restore) via run_process ---
        public static Task<string> DotnetRestoreTool(string rawArgs) =>
            RunProcessToolImpl.RunProcessToolAsync(JsonSerializer.Serialize(new { cmd = "dotnet", args = new[] { "restore", ExtractPath(rawArgs) } }));
        public static Task<string> DotnetBuildTool(string rawArgs)
        {
            using var doc = JsonDocument.Parse(rawArgs);
            var path = ExtractPath(rawArgs);
            var cfg = doc.RootElement.TryGetProperty("configuration", out var c) ? c.GetString() : "Debug";
            var tfm = doc.RootElement.TryGetProperty("framework", out var f) ? f.GetString() : null;

            var argList = new List<string> { "build", path, "-c", cfg };
            if (!string.IsNullOrWhiteSpace(tfm)) { argList.Add("-f"); argList.Add(tfm!); }
            return RunProcessToolImpl.RunProcessToolAsync(JsonSerializer.Serialize(new { cmd = "dotnet", args = argList.ToArray() }));
        }

        public static Task<string> DotnetTestTool(string rawArgs)
        {
            using var doc = JsonDocument.Parse(rawArgs);
            var path = ExtractPath(rawArgs);
            var filter = doc.RootElement.TryGetProperty("filter", out var f) ? f.GetString() : null;

            var args = new List<string> { "test", path, "--logger", "trx" };
            if (!string.IsNullOrWhiteSpace(filter)) { args.Add("--filter"); args.Add(filter!); }
            return RunProcessToolImpl.RunProcessToolAsync(JsonSerializer.Serialize(new { cmd = "dotnet", args = args.ToArray() }));
        }
        public static Task<string> DotnetRunTool(string rawArgs)
        {
            using var doc = JsonDocument.Parse(rawArgs);
            var path = ExtractPath(rawArgs);
            var filter = doc.RootElement.TryGetProperty("filter", out var f) ? f.GetString() : null;

            var args = new List<string> { "run", path, "--logger", "trx" };
            if (!string.IsNullOrWhiteSpace(filter)) { args.Add("--filter"); args.Add(filter!); }
            return RunProcessToolImpl.RunProcessToolAsync(JsonSerializer.Serialize(new { cmd = "dotnet", args = args.ToArray() }));
        }
        public static Task<string> DotnetNewTool(string rawArgs)
        {
            using var doc = JsonDocument.Parse(rawArgs);
            var workDir = thuvu.Models.AgentContext.GetEffectiveWorkDirectory();
            var path = ExtractPath(rawArgs);
            var template = doc.RootElement.TryGetProperty("template", out var t) ? t.GetString() : null;

            var args = new List<string> { "new",  template};
            var targetPath = Path.IsPathRooted(path) ? path : Path.Combine(workDir, path);
            Directory.CreateDirectory(targetPath);
            return RunProcessToolImpl.RunProcessToolAsync(JsonSerializer.Serialize(new { cmd = "dotnet", args = args.ToArray(), cwd = targetPath }));
        }
        public static string ExtractPath(string rawArgs)
        {
            using var doc = JsonDocument.Parse(rawArgs);
            return doc.RootElement.TryGetProperty("solution_or_project", out var p) && p.ValueKind == JsonValueKind.String
                ? (p.GetString() ?? "")
                : "";
        }

    }
}
