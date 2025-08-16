using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace thuvu.Tools
{
    public class SearchFilesToolImpl
    {
        public static string[] SearchFilesTool(string rawArgs)
        {
            using var doc = JsonDocument.Parse(rawArgs);
            var glob = doc.RootElement.TryGetProperty("glob", out var gEl) ? (gEl.GetString() ?? "**/*") : "**/*";
            var query = doc.RootElement.TryGetProperty("query", out var qEl) ? (qEl.GetString() ?? "") : "";

            // Detect a friendlier root than bin/Debug if possible
            var cwd = Directory.GetCurrentDirectory();
            var projectRoot = DetectProjectRoot(cwd) ?? cwd;

            // Split the glob into (rootDir, relative-pattern)
            var (rootDir, relPattern) = GetRootFromGlob(projectRoot, glob);

            var regex = GlobToRegex(relPattern);
            var results = new List<string>();
            const int MaxMatches = 500;
            const long MaxFileBytes = 2L * 1024 * 1024; // 2 MB

            var enumOpts = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MatchCasing = MatchCasing.PlatformDefault,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
            };

            foreach (var file in Directory.EnumerateFiles(rootDir, "*", enumOpts))
            {
                if (IsInExcludedDir(rootDir, file)) continue;

                var rel = Path.GetRelativePath(rootDir, file).Replace('\\', '/');
                if (!regex.IsMatch(rel)) continue;

                // If user only wants to list files (empty query), don't read files or size-filter
                if (string.IsNullOrWhiteSpace(query))
                {
                    results.Add(Path.GetFullPath(file));
                    if (results.Count >= MaxMatches) break;
                    continue;
                }

                // Otherwise, size-gate and content match
                try
                {
                    var info = new FileInfo(file);
                    if (!info.Exists || info.Length > MaxFileBytes) continue;
                }
                catch { continue; }

                if (SafeFileContains(file, query))
                {
                    results.Add(Path.GetFullPath(file));
                    if (results.Count >= MaxMatches) break;
                }
            }

            return results.ToArray();
        }
        public static string? DetectProjectRoot(string startDir)
        {
            try
            {
                var dir = new DirectoryInfo(startDir);
                while (dir != null)
                {
                    // Heuristics for a repo/project root
                    if (Directory.Exists(Path.Combine(dir.FullName, ".git"))) return dir.FullName;
                    if (Directory.EnumerateFiles(dir.FullName, "*.sln", SearchOption.TopDirectoryOnly).Any()) return dir.FullName;
                    if (File.Exists(Path.Combine(dir.FullName, "package.json"))) return dir.FullName;
                    if (Directory.Exists(Path.Combine(dir.FullName, "src"))) return dir.FullName;

                    dir = dir.Parent;
                }
            }
            catch { /* ignore */ }
            return null;
        }

        private static (string rootDir, string relPattern) GetRootFromGlob(string baseDir, string glob)
        {
            var norm = glob.Replace('\\', '/').Trim();

            // Absolute path pattern?
            if (Path.IsPathRooted(norm))
            {
                var firstWildcard = norm.IndexOfAny(new[] { '*', '?' });
                var basePath = firstWildcard >= 0
                    ? norm[..Math.Max(0, norm.LastIndexOf('/', Math.Max(0, firstWildcard - 1)))]
                    : (Path.GetDirectoryName(norm)?.Replace('\\', '/') ?? norm);
                if (string.IsNullOrEmpty(basePath)) basePath = "/";
                var rel = firstWildcard >= 0 ? norm.Substring(basePath.Length).TrimStart('/') : Path.GetFileName(norm);
                return (basePath, string.IsNullOrEmpty(rel) ? "**/*" : rel);
            }

            // Relative to the provided baseDir (project root)
            var first = norm.IndexOfAny(new[] { '*', '?' });
            if (first < 0) return (baseDir, norm);

            var rootPartEnd = norm.LastIndexOf('/', Math.Max(0, first - 1));
            var baseRel = rootPartEnd >= 0 ? norm[..rootPartEnd] : "";
            var baseAbs = string.IsNullOrEmpty(baseRel) ? baseDir : Path.GetFullPath(Path.Combine(baseDir, baseRel));
            var relPat = norm[(rootPartEnd + 1)..];
            return (baseAbs, string.IsNullOrEmpty(relPat) ? "**/*" : relPat);
        }

        private static System.Text.RegularExpressions.Regex GlobToRegex(string glob)
        {
            var g = glob.Replace('\\', '/').Trim();

            // Escape regex specials
            var sb = new System.Text.StringBuilder();
            foreach (var c in g)
            {
                if ("^$.+(){}[]|\\".Contains(c)) { sb.Append('\\').Append(c); }
                else sb.Append(c);
            }
            g = sb.ToString();

            // ** handling
            g = g.Replace("/**/", "/.*/");
            g = g.Replace("**/", ".*(/)?");
            g = g.Replace("/**", "(/.*)?");
            g = g.Replace("**", ".*");

            // * and ?
            g = g.Replace("*", "[^/]*");
            g = g.Replace("?", "[^/]");

            return new System.Text.RegularExpressions.Regex("^" + g + "$",
                System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        private static readonly string[] ExcludedDirNames = new[]
        {
    ".git", ".svn", ".hg", ".idea", ".vs",
    "bin", "obj", "node_modules", "packages", "dist", "build", "out", "target"
};

        private static bool IsInExcludedDir(string rootDir, string filePath)
        {
            var dir = Path.GetDirectoryName(filePath) ?? rootDir;
            while (dir.StartsWith(rootDir, StringComparison.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(dir);
                if (ExcludedDirNames.Any(ex => string.Equals(ex, name, StringComparison.OrdinalIgnoreCase)))
                    return true;

                var parent = Path.GetDirectoryName(dir);
                if (string.IsNullOrEmpty(parent) || parent == dir) break;
                dir = parent;
            }
            return false;
        }

        private static bool SafeFileContains(string path, string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return true;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                string? line;
                var cmp = StringComparison.OrdinalIgnoreCase;
                while ((line = sr.ReadLine()) != null)
                    if (line.IndexOf(query, cmp) >= 0) return true;
            }
            catch { }
            return false;
        }
    }
}
