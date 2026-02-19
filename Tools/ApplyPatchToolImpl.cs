using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using thuvu.Models;

namespace thuvu.Tools
{
    public class ApplyPatchToolImpl
    {
        // File extensions that should trigger code index updates
        private static readonly HashSet<string> IndexableExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".ts", ".js", ".tsx", ".jsx", ".py", ".go", ".java", ".rb", ".rs"
        };
        
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

                // Extract file path from patch for indexing later
                string? targetFile = ExtractTargetFilePath(patch);
                
                var (applied, rejects) = UnifiedDiff.Apply(patch, rootDir);
                
                if (applied)
                {
                    // Trigger code index update for the patched file
                    if (targetFile != null)
                    {
                        var fullPath = Path.Combine(rootDir, targetFile);
                        TriggerIndexUpdateAsync(fullPath);
                    }
                    
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
                    string? actualContent = null;
                    
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
                            
                            // Extract hunk line numbers and show actual content around them
                            var patchLines = patch.Split('\n');
                            foreach (var line in patchLines)
                            {
                                if (line.StartsWith("@@"))
                                {
                                    diagnostics.Add($"Hunk header: {line}");
                                    
                                    // Parse the start line from hunk header: @@ -N,M +N,M @@
                                    var match = System.Text.RegularExpressions.Regex.Match(line, @"@@ -(\d+)");
                                    if (match.Success && int.TryParse(match.Groups[1].Value, out int startLine))
                                    {
                                        // Show actual content around the failing hunk (5 lines before, 15 lines of context)
                                        int from = Math.Max(0, startLine - 6);
                                        int to = Math.Min(fileLines.Length - 1, startLine + 14);
                                        var sb = new StringBuilder();
                                        sb.AppendLine($"Actual content at lines {from + 1}-{to + 1}:");
                                        for (int i = from; i <= to; i++)
                                        {
                                            sb.AppendLine($"{i + 1,4}| {fileLines[i]}");
                                        }
                                        diagnostics.Add(sb.ToString().TrimEnd());
                                    }
                                }
                            }
                        }
                    }
                    
                    return JsonSerializer.Serialize(new 
                    { 
                        applied = false, 
                        rejects = string.IsNullOrEmpty(rejects) ? null : rejects,
                        diagnostics = diagnostics.Count > 0 ? diagnostics : null,
                        suggestion = "The patch context doesn't match the file. The actual file content around the failing hunk(s) is shown in diagnostics above. Use this to create a corrected patch."
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
            catch (JsonException ex)
            {
                // Detect truncation
                var isTruncated = ex.Message.Contains("end of data", StringComparison.OrdinalIgnoreCase) ||
                                  ex.Message.Contains("end of string", StringComparison.OrdinalIgnoreCase) ||
                                  ex.Message.Contains("unexpected end", StringComparison.OrdinalIgnoreCase);
                
                if (isTruncated)
                {
                    return JsonSerializer.Serialize(new 
                    { 
                        applied = false, 
                        error = "truncated_patch",
                        message = "The patch content was truncated by the LLM output limit. For large patches, apply changes in smaller increments.",
                        raw_args_length = rawArgs.Length
                    });
                }
                
                return JsonSerializer.Serialize(new 
                { 
                    applied = false, 
                    error = "invalid_arguments",
                    message = $"Failed to parse arguments: {ex.Message}"
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
        
        /// <summary>
        /// Extracts the target file path from a unified diff patch.
        /// </summary>
        private static string? ExtractTargetFilePath(string patch)
        {
            var lines = patch.Split('\n');
            foreach (var line in lines)
            {
                if (line.StartsWith("+++ "))
                {
                    var targetFile = line.Substring(4).Trim();
                    if (targetFile.StartsWith("b/")) targetFile = targetFile.Substring(2);
                    return targetFile;
                }
            }
            return null;
        }
        
        /// <summary>
        /// Triggers a code index update for a file if it's an indexable source file.
        /// This is fire-and-forget to not slow down patch operations.
        /// </summary>
        private static void TriggerIndexUpdateAsync(string fullPath)
        {
            try
            {
                var extension = Path.GetExtension(fullPath);
                if (SqliteConfig.Instance.Enabled && IndexableExtensions.Contains(extension))
                {
                    // Fire and forget - don't await
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await SqliteToolImpl.CodeIndexAsync(fullPath, force: true);
                        }
                        catch (Exception ex)
                        {
                            // Log but don't throw - indexing errors shouldn't affect patch success
                            Console.WriteLine($"[ApplyPatchToolImpl] Index update failed for {fullPath}: {ex.Message}");
                        }
                    });
                }
            }
            catch
            {
                // Ignore any errors in triggering the update
            }
        }
    }
}
