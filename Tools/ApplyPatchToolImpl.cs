using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace CodingAgent.BuildTools
{
    public class ApplyPatchToolImpl
    {
        public static string ApplyPatchTool(string rawArgs)
        {
            using var doc = JsonDocument.Parse(rawArgs);
            var patch = doc.RootElement.GetProperty("patch").GetString()!;
            var root = doc.RootElement.TryGetProperty("root", out var r) ? (r.GetString() ?? Directory.GetCurrentDirectory()) : Directory.GetCurrentDirectory();

            try
            {
                var (applied, rejects) = UnifiedDiff.Apply(patch, root); // implement tiny patcher or use your favorite lib
                return JsonSerializer.Serialize(new { applied, rejects = string.IsNullOrEmpty(rejects) ? null : rejects });
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { applied = false, error = ex.Message });
            }
        }
    }
}
