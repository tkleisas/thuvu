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
        // Maximum file size to read (1MB default, configurable)
        private const int MaxFileSizeBytes = 1024 * 1024;
        
        // Maximum lines to return without line range
        private const int MaxLinesDefault = 2000;

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
            try
            {
                using var doc = JsonDocument.Parse(rawArgs);
                var root = doc.RootElement;
                var workDir = thuvu.Models.AgentContext.GetEffectiveWorkDirectory();
                var path = root.GetProperty("path").GetString()!;
                
                // Resolve relative paths against work directory
                var fullPath = Path.IsPathRooted(path) ? path : Path.Combine(workDir, path);
                fullPath = Path.GetFullPath(fullPath);
                
                // Check file exists
                if (!File.Exists(fullPath))
                {
                    return JsonSerializer.Serialize(new
                    {
                        error = "file_not_found",
                        message = $"File not found: {path}",
                        resolved_path = fullPath
                    });
                }
                
                // Get file info
                var fileInfo = new FileInfo(fullPath);
                
                // Check if binary
                if (IsBinaryFile(fullPath))
                {
                    return JsonSerializer.Serialize(new
                    {
                        error = "binary_file",
                        message = "Cannot read binary file as text",
                        path = path,
                        size_bytes = fileInfo.Length
                    });
                }
                
                // Parse optional parameters
                int? startLine = root.TryGetProperty("start_line", out var sl) && sl.ValueKind == JsonValueKind.Number 
                    ? sl.GetInt32() : null;
                int? endLine = root.TryGetProperty("end_line", out var el) && el.ValueKind == JsonValueKind.Number 
                    ? el.GetInt32() : null;
                bool includeLineNumbers = root.TryGetProperty("line_numbers", out var ln) && ln.ValueKind == JsonValueKind.True;
                int maxLines = root.TryGetProperty("max_lines", out var ml) && ml.ValueKind == JsonValueKind.Number 
                    ? ml.GetInt32() : MaxLinesDefault;
                
                // Check file size for full reads
                if (fileInfo.Length > MaxFileSizeBytes && startLine == null)
                {
                    return JsonSerializer.Serialize(new
                    {
                        error = "file_too_large",
                        message = $"File is {fileInfo.Length:N0} bytes. Use start_line/end_line to read a portion, or increase max_lines.",
                        path = path,
                        size_bytes = fileInfo.Length,
                        suggestion = "Try read_file with start_line=1, end_line=500 to read first 500 lines"
                    });
                }
                
                // Read the file
                var allLines = File.ReadAllLines(fullPath);
                var totalLines = allLines.Length;
                
                // Apply line range
                int start = Math.Max(1, startLine ?? 1);
                int end = Math.Min(totalLines, endLine ?? totalLines);
                
                // Clamp to max lines if no explicit range
                if (startLine == null && endLine == null && totalLines > maxLines)
                {
                    end = maxLines;
                }
                
                // Extract requested lines (1-indexed)
                var selectedLines = allLines.Skip(start - 1).Take(end - start + 1).ToArray();
                
                // Build content with optional line numbers
                string content;
                if (includeLineNumbers)
                {
                    var sb = new StringBuilder();
                    for (int i = 0; i < selectedLines.Length; i++)
                    {
                        int lineNum = start + i;
                        sb.AppendLine($"{lineNum,5}: {selectedLines[i]}");
                    }
                    content = sb.ToString();
                }
                else
                {
                    content = string.Join(Environment.NewLine, selectedLines);
                }
                
                // Calculate SHA256 of full file for checksum operations
                var fullContent = string.Join(Environment.NewLine, allLines);
                var sha256 = Sha256(fullContent);
                
                var result = new Dictionary<string, object>
                {
                    ["content"] = content,
                    ["sha256"] = sha256,
                    ["encoding"] = "utf-8",
                    ["total_lines"] = totalLines,
                    ["lines_returned"] = selectedLines.Length,
                    ["start_line"] = start,
                    ["end_line"] = end
                };
                
                // Add truncation warning
                if (end < totalLines && endLine == null)
                {
                    result["truncated"] = true;
                    result["message"] = $"File truncated. Showing lines {start}-{end} of {totalLines}. Use start_line/end_line to read more.";
                }
                
                return JsonSerializer.Serialize(result);
            }
            catch (UnauthorizedAccessException)
            {
                return JsonSerializer.Serialize(new
                {
                    error = "permission_denied",
                    message = "Permission denied reading file"
                });
            }
            catch (IOException ex)
            {
                return JsonSerializer.Serialize(new
                {
                    error = "io_error",
                    message = ex.Message
                });
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new
                {
                    error = "unexpected_error",
                    message = ex.Message
                });
            }
        }
        
        /// <summary>
        /// Check if a file appears to be binary by looking for null bytes
        /// </summary>
        private static bool IsBinaryFile(string path)
        {
            try
            {
                // Read first 8KB to check for binary content
                using var stream = File.OpenRead(path);
                var buffer = new byte[8192];
                var bytesRead = stream.Read(buffer, 0, buffer.Length);
                
                // Check for null bytes (strong indicator of binary)
                for (int i = 0; i < bytesRead; i++)
                {
                    if (buffer[i] == 0) return true;
                }
                
                // Check ratio of non-printable characters
                int nonPrintable = 0;
                for (int i = 0; i < bytesRead; i++)
                {
                    byte b = buffer[i];
                    // Allow common text characters: printable ASCII, tab, newline, carriage return
                    if (b < 32 && b != 9 && b != 10 && b != 13)
                    {
                        nonPrintable++;
                    }
                }
                
                // If more than 10% non-printable, likely binary
                return bytesRead > 0 && (double)nonPrintable / bytesRead > 0.1;
            }
            catch
            {
                return false; // Assume text if we can't check
            }
        }
    }
}
