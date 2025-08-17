using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace thuvu.Tools
{
    public class Helpers
    {
        // git_status: honor optional { "root": "<path>" }
        public static object BuildGitStatusArgs(string rawArgs)
        {
            using var doc = JsonDocument.Parse(rawArgs);
            string? cwd = doc.RootElement.TryGetProperty("root", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString()
                : null;

            return new
            {
                cmd = "git",
                args = new[] { "status", "--porcelain", "-b" },
                cwd
            };
        }

        // git_diff: supports { "paths":[], "staged":bool, "context":int, "root":string? }
        public static object BuildGitDiffArgs(string rawArgs)
        {
            using var doc = JsonDocument.Parse(rawArgs);
            var root = doc.RootElement;

            var args = new List<string> { "diff" };

            if (root.TryGetProperty("staged", out var stagedEl) && stagedEl.ValueKind == JsonValueKind.True)
                args.Add("--staged"); // (aka --cached)

            if (root.TryGetProperty("context", out var ctxEl) && ctxEl.ValueKind == JsonValueKind.Number)
            {
                var ctx = Math.Clamp(ctxEl.GetInt32(), 0, 100);
                args.Add("-U");
                args.Add(ctx.ToString());
            }

            var paths = new List<string>();
            if (root.TryGetProperty("paths", out var pEl) && pEl.ValueKind == JsonValueKind.Array)
                foreach (var it in pEl.EnumerateArray())
                    if (it.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(it.GetString()))
                        paths.Add(it.GetString()!);

            if (paths.Count > 0)
            {
                args.Add("--");
                args.AddRange(paths);
            }

            string? cwd = root.TryGetProperty("root", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString()
                : null;

            return new { cmd = "git", args = args.ToArray(), cwd };
        }

        // nuget_add: { "id": "Package.Id", "version": "x.y.z"?, "project": "path/to.csproj"? }
        public static object BuildNugetAddArgs(string rawArgs)
        {
            using var doc = JsonDocument.Parse(rawArgs);
            var root = doc.RootElement;

            var id = root.GetProperty("id").GetString() ?? throw new ArgumentException("nuget_add.id is required.");
            var version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var project = root.TryGetProperty("project", out var pr) && pr.ValueKind == JsonValueKind.String ? pr.GetString() : null;

            var args = new List<string> { "add" };
            if (!string.IsNullOrWhiteSpace(project)) args.Add(project);
            args.Add("package");
            args.Add(id);
            if (!string.IsNullOrWhiteSpace(version))
            {
                args.Add("--version");
                args.Add(version!);
            }

            return new { cmd = "dotnet", args = args.ToArray() };
        }

        // nuget_search helper
        public static string ExtractQuery(string rawArgs)
        {
            using var doc = JsonDocument.Parse(rawArgs);
            return doc.RootElement.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String
                ? (q.GetString() ?? "")
                : "";
        }

        public static string GetCurrentGitTag()
        {
            //var info = Assembly.GetExecutingAssembly()
            //.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            //?.InformationalVersion ?? "unknown";
            var info = ThisBuild.GitTag;

            return info;
        }

    }
}
