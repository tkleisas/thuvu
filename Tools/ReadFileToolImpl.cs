using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace thuvu.Tools
{
    public class ReadFileToolImpl
    {
        public static string ReadAllTextSafe(string path) =>
    File.ReadAllText(path);

        public static string Sha256(string text)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            var hash = sha.ComputeHash(bytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        public static string ReadFileTool(string rawArgs)
        {
            using var doc = JsonDocument.Parse(rawArgs);
            var workDir = thuvu.Models.AgentConfig.GetWorkDirectory();
            var path = doc.RootElement.GetProperty("path").GetString()!;
            // Resolve relative paths against work directory
            var fullPath = Path.IsPathRooted(path) ? path : Path.Combine(workDir, path);
            var content = ReadAllTextSafe(fullPath);
            return JsonSerializer.Serialize(new
            {
                content,
                sha256 = Sha256(content),
                encoding = "utf-8"
            });
        }
    }
}
