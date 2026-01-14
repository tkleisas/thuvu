using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace thuvu.Tools
{
    public class ApplyPatchToolImpl
    {
        public static string ApplyPatchTool(string rawArgs)
        {
            try
            {
                using var doc = JsonDocument.Parse(rawArgs);
                var root = doc.RootElement;
                
                if (!root.TryGetProperty("patch", out var patchEl) || patchEl.ValueKind != JsonValueKind.String)
                {
                    return JsonSerializer.Serialize(new 
                    { 
                        applied = false, 
                        error = "missing_patch",
                        message = "patch is required"
                    });
                }
                
                var patch = patchEl.GetString()!;
                var workDir = thuvu.Models.AgentContext.GetEffectiveWorkDirectory();
                var rootDir = root.TryGetProperty("root", out var r) && r.ValueKind == JsonValueKind.String 
                    ? (r.GetString() ?? workDir) : workDir;

                // Validate patch format
                if (!patch.Contains("---") || !patch.Contains("+++"))
                {
                    return JsonSerializer.Serialize(new 
                    { 
                        applied = false, 
                        error = "invalid_patch_format",
                        message = "Patch must be in unified diff format with --- and +++ headers"
                    });
                }

                var (applied, rejects) = UnifiedDiff.Apply(patch, rootDir);
                
                if (applied)
                {
                    return JsonSerializer.Serialize(new 
                    { 
                        applied = true,
                        message = "Patch applied successfully"
                    });
                }
                else
                {
                    // Try to provide helpful diagnostics
                    var diagnostics = new List<string>();
                    
                    // Extract file path from patch
                    var lines = patch.Split('\n');
                    string? targetFile = null;
                    foreach (var line in lines)
                    {
                        if (line.StartsWith("+++ "))
                        {
                            targetFile = line.Substring(4).Trim();
                            if (targetFile.StartsWith("b/")) targetFile = targetFile.Substring(2);
                            break;
                        }
                    }
                    
                    if (targetFile != null)
                    {
                        var fullPath = Path.Combine(rootDir, targetFile);
                        if (!File.Exists(fullPath))
                        {
                            diagnostics.Add($"Target file not found: {fullPath}");
                        }
                        else
                        {
                            var fileLines = File.ReadAllLines(fullPath);
                            diagnostics.Add($"File has {fileLines.Length} lines");
                            
                            // Try to find the context that didn't match
                            foreach (var line in lines)
                            {
                                if (line.StartsWith("@@"))
                                {
                                    diagnostics.Add($"Hunk header: {line}");
                                }
                            }
                        }
                    }
                    
                    return JsonSerializer.Serialize(new 
                    { 
                        applied = false, 
                        rejects = string.IsNullOrEmpty(rejects) ? null : rejects,
                        diagnostics = diagnostics.Count > 0 ? diagnostics : null,
                        suggestion = "The patch context doesn't match the file. Read the file first with read_file(path, line_numbers=true) to see actual line numbers and content."
                    });
                }
            }
            catch (FileNotFoundException ex)
            {
                return JsonSerializer.Serialize(new 
                { 
                    applied = false, 
                    error = "file_not_found",
                    message = ex.Message
                });
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new 
                { 
                    applied = false, 
                    error = "unexpected_error",
                    message = ex.Message
                });
            }
        }
    }
}
