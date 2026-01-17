using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using thuvu.Models;

namespace thuvu.Tools
{
    public class WriteFileToolImpl
    {
        // Maximum file size to write (10MB)
        private const int MaxFileSizeBytes = 10 * 1024 * 1024;
        
        // Warning threshold for content that might get truncated by LLM (6KB - conservative)
        private const int ContentWarningThreshold = 6000;
        
        // File extensions that should trigger code index updates
        private static readonly HashSet<string> IndexableExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".ts", ".js", ".tsx", ".jsx", ".py", ".go", ".java", ".rb", ".rs"
        };
        
        // Track ongoing chunked writes: path -> (content so far, expected total chunks)
        private static readonly Dictionary<string, ChunkedWriteState> _chunkedWrites = new();
        
        private class ChunkedWriteState
        {
            public StringBuilder Content { get; } = new();
            public int TotalChunks { get; set; }
            public int ReceivedChunks { get; set; }
            public DateTime StartedAt { get; set; } = DateTime.Now;
            public string? ExpectedSha256 { get; set; }
        }

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
                
                // Write atomically: write to temp file first, then rename
                // This prevents corruption if write is interrupted
                string? tempPath = null;
                string? backupPath = null;
                
                try
                {
                    // Write to temporary file first
                    tempPath = fullPath + ".tmp." + Guid.NewGuid().ToString("N")[..8];
                    File.WriteAllText(tempPath, content);
                    
                    // Verify the temp file was written correctly
                    var writtenContent = File.ReadAllText(tempPath);
                    if (writtenContent != content)
                    {
                        File.Delete(tempPath);
                        return JsonSerializer.Serialize(new 
                        { 
                            wrote = false, 
                            error = "write_verification_failed",
                            message = "File content verification failed after write"
                        });
                    }
                    
                    // If file exists, create backup before replacing
                    if (fileExists)
                    {
                        backupPath = fullPath + ".bak." + DateTime.Now.ToString("yyyyMMddHHmmss");
                        File.Copy(fullPath, backupPath, overwrite: true);
                    }
                    
                    // Atomic replace: delete original, rename temp to original
                    if (fileExists)
                    {
                        File.Delete(fullPath);
                    }
                    File.Move(tempPath, fullPath);
                    tempPath = null; // Successfully moved, don't delete in finally
                    
                    // Clean up backup on success (unless explicitly requested)
                    if (backupPath != null && !createBackup)
                    {
                        try { File.Delete(backupPath); } catch { /* ignore cleanup errors */ }
                        backupPath = null;
                    }
                }
                catch (Exception ex)
                {
                    // Restore from backup if available
                    if (backupPath != null && File.Exists(backupPath))
                    {
                        try
                        {
                            if (File.Exists(fullPath))
                                File.Delete(fullPath);
                            File.Move(backupPath, fullPath);
                            
                            return JsonSerializer.Serialize(new 
                            { 
                                wrote = false, 
                                error = "write_failed_restored",
                                message = $"Write failed ({ex.Message}). Original file has been restored.",
                                restored = true
                            });
                        }
                        catch
                        {
                            return JsonSerializer.Serialize(new 
                            { 
                                wrote = false, 
                                error = "write_failed_restore_failed",
                                message = $"Write failed ({ex.Message}). WARNING: Could not restore original. Backup at: {backupPath}",
                                backup_path = backupPath
                            });
                        }
                    }
                    
                    throw; // Re-throw to be handled by outer catch blocks
                }
                finally
                {
                    // Clean up temp file if it still exists (write failed before move)
                    if (tempPath != null && File.Exists(tempPath))
                    {
                        try { File.Delete(tempPath); } catch { /* ignore */ }
                    }
                }
                
                // Calculate new checksum
                var newSha256 = ReadFileToolImpl.Sha256(content);
                var lineCount = content.Split('\n').Length;
                
                // Trigger code index update for source files (fire and forget)
                TriggerIndexUpdateAsync(fullPath);
                
                // Build response with optional warning
                var response = new Dictionary<string, object>
                {
                    ["wrote"] = true,
                    ["path"] = fullPath,
                    ["size_bytes"] = contentBytes,
                    ["lines"] = lineCount,
                    ["sha256"] = newSha256,
                    ["was_new_file"] = !fileExists
                };
                
                if (previousSha256 != null)
                    response["previous_sha256"] = previousSha256;
                    
                if (backupPath != null)
                    response["backup_path"] = backupPath;
                
                // Add warning for future large writes
                if (contentBytes > ContentWarningThreshold)
                {
                    response["warning"] = $"This file is {contentBytes:N0} bytes. For files over {ContentWarningThreshold:N0} bytes, consider using write_file_chunk to avoid truncation issues.";
                }
                
                return JsonSerializer.Serialize(response);
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
                // Detect truncation - common patterns are "end of data" or unexpected end
                var isTruncated = ex.Message.Contains("end of data", StringComparison.OrdinalIgnoreCase) ||
                                  ex.Message.Contains("end of string", StringComparison.OrdinalIgnoreCase) ||
                                  ex.Message.Contains("unexpected end", StringComparison.OrdinalIgnoreCase);
                
                if (isTruncated)
                {
                    return JsonSerializer.Serialize(new 
                    { 
                        wrote = false, 
                        error = "truncated_content",
                        message = "The file content was truncated by the LLM output limit. Use write_file_chunk tool to write large files in multiple chunks.",
                        raw_args_length = rawArgs.Length,
                        suggestion = "Split the content into chunks of ~4KB each using write_file_chunk(path, content, chunk_number, total_chunks)"
                    });
                }
                
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
        
        /// <summary>
        /// Write a file in chunks to avoid LLM output truncation.
        /// </summary>
        public static string WriteFileChunkTool(string rawArgs)
        {
            try
            {
                using var doc = JsonDocument.Parse(rawArgs);
                var root = doc.RootElement;
                var workDir = thuvu.Models.AgentContext.GetEffectiveWorkDirectory();

                // Get path
                if (!root.TryGetProperty("path", out var pathEl) || pathEl.ValueKind != JsonValueKind.String)
                {
                    return JsonSerializer.Serialize(new { success = false, error = "missing_path", message = "path is required" });
                }
                var path = pathEl.GetString()!;
                
                // Resolve relative paths
                var fullPath = Path.IsPathRooted(path) ? path : Path.Combine(workDir, path);
                fullPath = Path.GetFullPath(fullPath);

                // Get chunk content
                if (!root.TryGetProperty("content", out var contentEl) || contentEl.ValueKind != JsonValueKind.String)
                {
                    return JsonSerializer.Serialize(new { success = false, error = "missing_content", message = "content is required" });
                }
                var content = contentEl.GetString()!;
                
                // Get chunk number (1-indexed)
                if (!root.TryGetProperty("chunk_number", out var chunkNumEl) || chunkNumEl.ValueKind != JsonValueKind.Number)
                {
                    return JsonSerializer.Serialize(new { success = false, error = "missing_chunk_number", message = "chunk_number is required" });
                }
                var chunkNumber = chunkNumEl.GetInt32();
                
                // Get total chunks
                if (!root.TryGetProperty("total_chunks", out var totalEl) || totalEl.ValueKind != JsonValueKind.Number)
                {
                    return JsonSerializer.Serialize(new { success = false, error = "missing_total_chunks", message = "total_chunks is required" });
                }
                var totalChunks = totalEl.GetInt32();
                
                // Optional: expected_sha256 for first chunk (to check existing file)
                var expectedSha256 = root.TryGetProperty("expected_sha256", out var e) && e.ValueKind == JsonValueKind.String
                    ? e.GetString() : null;

                // Validate chunk number
                if (chunkNumber < 1 || chunkNumber > totalChunks)
                {
                    return JsonSerializer.Serialize(new { success = false, error = "invalid_chunk_number", message = $"chunk_number must be between 1 and {totalChunks}" });
                }
                
                // First chunk - initialize state
                if (chunkNumber == 1)
                {
                    // Check existing file if expected_sha256 provided
                    if (expectedSha256 != null && File.Exists(fullPath))
                    {
                        var currentContent = File.ReadAllText(fullPath);
                        var currentSha256 = ReadFileToolImpl.Sha256(currentContent);
                        
                        if (!string.Equals(currentSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
                        {
                            return JsonSerializer.Serialize(new 
                            { 
                                success = false, 
                                error = "checksum_mismatch",
                                message = "File has been modified since last read.",
                                expected_sha256 = expectedSha256,
                                actual_sha256 = currentSha256
                            });
                        }
                    }
                    
                    _chunkedWrites[fullPath] = new ChunkedWriteState
                    {
                        TotalChunks = totalChunks,
                        ExpectedSha256 = expectedSha256
                    };
                }
                
                // Get or validate state
                if (!_chunkedWrites.TryGetValue(fullPath, out var state))
                {
                    return JsonSerializer.Serialize(new { success = false, error = "no_chunked_write", message = "No chunked write in progress for this file. Start with chunk_number=1." });
                }
                
                if (chunkNumber != state.ReceivedChunks + 1)
                {
                    return JsonSerializer.Serialize(new { success = false, error = "wrong_chunk_order", message = $"Expected chunk {state.ReceivedChunks + 1}, got {chunkNumber}" });
                }
                
                // Append content
                state.Content.Append(content);
                state.ReceivedChunks++;
                
                // Check if this is the last chunk
                if (chunkNumber == totalChunks)
                {
                    // Write the complete file
                    var fullContent = state.Content.ToString();
                    var contentBytes = Encoding.UTF8.GetByteCount(fullContent);
                    
                    if (contentBytes > MaxFileSizeBytes)
                    {
                        _chunkedWrites.Remove(fullPath);
                        return JsonSerializer.Serialize(new 
                        { 
                            success = false, 
                            error = "content_too_large", 
                            message = $"Total content is {contentBytes:N0} bytes, max is {MaxFileSizeBytes:N0} bytes"
                        });
                    }
                    
                    // Ensure parent directory exists
                    var parentDir = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                    {
                        Directory.CreateDirectory(parentDir);
                    }
                    
                    bool fileExists = File.Exists(fullPath);
                    string? tempPath = null;
                    string? backupPath = null;
                    
                    try
                    {
                        // Write to temp file first for atomic operation
                        tempPath = fullPath + ".tmp." + Guid.NewGuid().ToString("N")[..8];
                        File.WriteAllText(tempPath, fullContent);
                        
                        // Create backup of existing file
                        if (fileExists)
                        {
                            backupPath = fullPath + ".bak." + DateTime.Now.ToString("yyyyMMddHHmmss");
                            File.Copy(fullPath, backupPath, overwrite: true);
                            File.Delete(fullPath);
                        }
                        
                        File.Move(tempPath, fullPath);
                        tempPath = null;
                        
                        // Clean up backup on success
                        if (backupPath != null)
                        {
                            try { File.Delete(backupPath); } catch { }
                            backupPath = null;
                        }
                    }
                    catch (Exception ex)
                    {
                        // Restore from backup if available
                        if (backupPath != null && File.Exists(backupPath))
                        {
                            try
                            {
                                if (File.Exists(fullPath)) File.Delete(fullPath);
                                File.Move(backupPath, fullPath);
                                _chunkedWrites.Remove(fullPath);
                                return JsonSerializer.Serialize(new 
                                { 
                                    success = false, 
                                    error = "write_failed_restored",
                                    message = $"Write failed ({ex.Message}). Original file has been restored."
                                });
                            }
                            catch
                            {
                                _chunkedWrites.Remove(fullPath);
                                return JsonSerializer.Serialize(new 
                                { 
                                    success = false, 
                                    error = "write_failed_restore_failed",
                                    message = $"Write failed. Backup at: {backupPath}"
                                });
                            }
                        }
                        throw;
                    }
                    finally
                    {
                        if (tempPath != null && File.Exists(tempPath))
                        {
                            try { File.Delete(tempPath); } catch { }
                        }
                    }
                    
                    var newSha256 = ReadFileToolImpl.Sha256(fullContent);
                    var lineCount = fullContent.Split('\n').Length;
                    
                    _chunkedWrites.Remove(fullPath);
                    
                    // Trigger code index update for source files (fire and forget)
                    TriggerIndexUpdateAsync(fullPath);
                    
                    return JsonSerializer.Serialize(new
                    { 
                        success = true,
                        complete = true,
                        path = fullPath,
                        size_bytes = contentBytes,
                        lines = lineCount,
                        sha256 = newSha256,
                        was_new_file = !fileExists,
                        chunks_received = totalChunks
                    });
                }
                else
                {
                    // More chunks expected
                    return JsonSerializer.Serialize(new 
                    { 
                        success = true,
                        complete = false,
                        chunk_received = chunkNumber,
                        chunks_remaining = totalChunks - chunkNumber,
                        bytes_so_far = state.Content.Length
                    });
                }
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
                        success = false, 
                        error = "truncated_content",
                        message = "The chunk content was truncated by the LLM output limit. Each chunk MUST be under 4KB (~100 lines of code). Split your content into more chunks.",
                        raw_args_length = rawArgs.Length,
                        suggestion = "If writing a 200-line file, use 2 chunks of 100 lines each. Recalculate total_chunks and retry."
                    });
                }
                
                return JsonSerializer.Serialize(new { success = false, error = "invalid_arguments", message = $"Failed to parse arguments: {ex.Message}" });
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { success = false, error = "unexpected_error", message = ex.Message });
            }
        }
        
        /// <summary>
        /// Cancel an in-progress chunked write.
        /// </summary>
        public static string CancelChunkedWriteTool(string rawArgs)
        {
            try
            {
                using var doc = JsonDocument.Parse(rawArgs);
                var root = doc.RootElement;
                var workDir = thuvu.Models.AgentContext.GetEffectiveWorkDirectory();

                if (!root.TryGetProperty("path", out var pathEl) || pathEl.ValueKind != JsonValueKind.String)
                {
                    return JsonSerializer.Serialize(new { success = false, error = "missing_path" });
                }
                var path = pathEl.GetString()!;
                var fullPath = Path.IsPathRooted(path) ? path : Path.Combine(workDir, path);
                fullPath = Path.GetFullPath(fullPath);
                
                if (_chunkedWrites.Remove(fullPath))
                {
                    return JsonSerializer.Serialize(new { success = true, message = "Chunked write cancelled" });
                }
                else
                {
                    return JsonSerializer.Serialize(new { success = false, message = "No chunked write in progress for this file" });
                }
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { success = false, error = ex.Message });
            }
        }
        
        /// <summary>
        /// Clean up stale chunked writes (older than 10 minutes).
        /// </summary>
        public static void CleanupStaleChunkedWrites()
        {
            var cutoff = DateTime.Now.AddMinutes(-10);
            var staleKeys = _chunkedWrites
                .Where(kvp => kvp.Value.StartedAt < cutoff)
                .Select(kvp => kvp.Key)
                .ToList();
            
            foreach (var key in staleKeys)
            {
                _chunkedWrites.Remove(key);
                Console.WriteLine($"[WriteFileToolImpl] Cleaned up stale chunked write for: {key}");
            }
        }
        
        /// <summary>
        /// Triggers a code index update for a file if it's an indexable source file.
        /// This is fire-and-forget to not slow down write operations.
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
                            // Log but don't throw - indexing errors shouldn't affect write success
                            Console.WriteLine($"[WriteFileToolImpl] Index update failed for {fullPath}: {ex.Message}");
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
