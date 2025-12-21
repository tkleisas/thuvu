using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace thuvu.Tools
{
    public class WriteFileToolImpl
    {
        // --- write_file ---
        public static string WriteFileTool(string rawArgs)
        {
            using var doc = JsonDocument.Parse(rawArgs);
            var root = doc.RootElement;
            var workDir = thuvu.Models.AgentContext.GetEffectiveWorkDirectory();

            var path = root.GetProperty("path").GetString()!;
            // Resolve relative paths against work directory
            var fullPath = Path.IsPathRooted(path) ? path : Path.Combine(workDir, path);
            var content = root.GetProperty("content").GetString()!;
            var expected = root.TryGetProperty("expected_sha256", out var e) && e.ValueKind == JsonValueKind.String
                           ? e.GetString() : null;
            var createDirs = root.TryGetProperty("create_intermediate_dirs", out var c) && c.GetBoolean();

            if (createDirs)
            {
                var dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            }

            if (File.Exists(fullPath) && expected != null)
            {
                var current = ReadFileToolImpl.ReadAllTextSafe(fullPath);
                var currentHash = ReadFileToolImpl.Sha256(current);
                if (!string.Equals(currentHash, expected, StringComparison.OrdinalIgnoreCase))
                {
                    return JsonSerializer.Serialize(new { wrote = false, error = "checksum_mismatch" });
                }
            }

            // Ensure parent directory exists
            var parentDir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(parentDir)) Directory.CreateDirectory(parentDir);
            
            File.WriteAllText(fullPath, content);
            return JsonSerializer.Serialize(new { wrote = true });
        }
    }

}
