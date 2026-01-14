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
        // Maximum file size to write (10MB)
        private const int MaxFileSizeBytes = 10 * 1024 * 1024;

        public static string WriteFileTool(string rawArgs)
        {
            try
            {
                using var doc = JsonDocument.Parse(rawArgs);
                var root = doc.RootElement;
                var workDir = thuvu.Models.AgentContext.GetEffectiveWorkDirectory();

                // Get path
                if (!root.TryGetProperty("path", out var pathEl) || pathEl.ValueKind != JsonValueKind.String)
                {
                    return JsonSerializer.Serialize(new { wrote = false, error = "missing_path", message = "path is required" });
                }
                var path = pathEl.GetString()!;
                
                // Resolve relative paths against work directory
                var fullPath = Path.IsPathRooted(path) ? path : Path.Combine(workDir, path);
                fullPath = Path.GetFullPath(fullPath);

                // Get content
                if (!root.TryGetProperty("content", out var contentEl) || contentEl.ValueKind != JsonValueKind.String)
                {
                    return JsonSerializer.Serialize(new { wrote = false, error = "missing_content", message = "content is required" });
                }
                var content = contentEl.GetString()!;
                
                // Check content size
                var contentBytes = Encoding.UTF8.GetByteCount(content);
                if (contentBytes > MaxFileSizeBytes)
                {
                    return JsonSerializer.Serialize(new 
                    { 
                        wrote = false, 
                        error = "content_too_large", 
                        message = $"Content is {contentBytes:N0} bytes, max is {MaxFileSizeBytes:N0} bytes",
                        size_bytes = contentBytes
                    });
                }

                // Optional parameters
                var expected = root.TryGetProperty("expected_sha256", out var e) && e.ValueKind == JsonValueKind.String
                               ? e.GetString() : null;
                var createDirs = !root.TryGetProperty("create_intermediate_dirs", out var c) || c.ValueKind != JsonValueKind.False;
                var createBackup = root.TryGetProperty("backup", out var b) && b.ValueKind == JsonValueKind.True;

                // Check if file exists and validate checksum if provided
                bool fileExists = File.Exists(fullPath);
                string? previousSha256 = null;
                
                if (fileExists)
                {
                    var currentContent = File.ReadAllText(fullPath);
                    previousSha256 = ReadFileToolImpl.Sha256(currentContent);
                    
                    if (expected != null && !string.Equals(previousSha256, expected, StringComparison.OrdinalIgnoreCase))
                    {
                        return JsonSerializer.Serialize(new 
                        { 
                            wrote = false, 
                            error = "checksum_mismatch",
                            message = "File has been modified since last read. Re-read the file to get current checksum.",
                            expected_sha256 = expected,
                            actual_sha256 = previousSha256
                        });
                    }
                    
                    // Create backup if requested
                    if (createBackup)
                    {
                        var backupPath = fullPath + ".bak";
                        File.Copy(fullPath, backupPath, overwrite: true);
                    }
                }

                // Ensure parent directory exists
                var parentDir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(parentDir))
                {
                    if (!Directory.Exists(parentDir))
                    {
                        if (createDirs)
                        {
                            Directory.CreateDirectory(parentDir);
                        }
                        else
                        {
                            return JsonSerializer.Serialize(new 
                            { 
                                wrote = false, 
                                error = "directory_not_found",
                                message = $"Parent directory does not exist: {parentDir}. Set create_intermediate_dirs=true to create it."
                            });
                        }
                    }
                }
                
                // Write the file
                File.WriteAllText(fullPath, content);
                
                // Calculate new checksum
                var newSha256 = ReadFileToolImpl.Sha256(content);
                var lineCount = content.Split('\n').Length;
                
                return JsonSerializer.Serialize(new 
                { 
                    wrote = true,
                    path = fullPath,
                    size_bytes = contentBytes,
                    lines = lineCount,
                    sha256 = newSha256,
                    was_new_file = !fileExists,
                    previous_sha256 = previousSha256
                });
            }
            catch (UnauthorizedAccessException)
            {
                return JsonSerializer.Serialize(new 
                { 
                    wrote = false, 
                    error = "permission_denied",
                    message = "Permission denied writing to file"
                });
            }
            catch (DirectoryNotFoundException ex)
            {
                return JsonSerializer.Serialize(new 
                { 
                    wrote = false, 
                    error = "directory_not_found",
                    message = ex.Message
                });
            }
            catch (IOException ex)
            {
                return JsonSerializer.Serialize(new 
                { 
                    wrote = false, 
                    error = "io_error",
                    message = ex.Message
                });
            }
            catch (JsonException ex)
            {
                return JsonSerializer.Serialize(new 
                { 
                    wrote = false, 
                    error = "invalid_arguments",
                    message = $"Failed to parse arguments: {ex.Message}"
                });
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new 
                { 
                    wrote = false, 
                    error = "unexpected_error",
                    message = ex.Message
                });
            }
        }
    }
}
