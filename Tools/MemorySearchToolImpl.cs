using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace thuvu.Tools
{
    /// <summary>
    /// Tool implementation for searching past conversation messages (memory).
    /// Uses SQLite FTS5 full-text search to find relevant messages across sessions.
    /// </summary>
    public static class MemorySearchToolImpl
    {
        /// <summary>
        /// Search past conversation messages for relevant context.
        /// </summary>
        public static async Task<string> SearchAsync(
            string query, 
            string? sessionId = null,
            int limit = 10,
            bool includeCurrentContext = false,
            CancellationToken ct = default)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(query))
                    return "Error: query parameter is required";

                limit = Math.Clamp(limit, 1, 50);

                var sqlite = SqliteService.Instance;
                if (sqlite == null)
                    return "Error: SQLite service not initialized. Memory search requires SQLite to be enabled.";

                var results = await sqlite.SearchMemoryAsync(query, sessionId, limit, includeCurrentContext, ct);

                if (results.Count == 0)
                    return $"No past messages found matching '{query}'. The FTS index may need rebuilding — try memory_rebuild_index first if this is a fresh database.";

                var sb = new StringBuilder();
                sb.AppendLine($"Found {results.Count} matching message(s) for '{query}':");
                sb.AppendLine();

                foreach (var result in results)
                {
                    var sessionLabel = result.IsCurrentSession ? "CURRENT SESSION" : (result.SessionTitle ?? result.SessionId[..Math.Min(8, result.SessionId.Length)]);
                    var summarizedLabel = result.IsSummarized ? " [summarized]" : "";
                    
                    sb.AppendLine($"--- [{sessionLabel}] {result.Timestamp:yyyy-MM-dd HH:mm}{summarizedLabel} ---");
                    
                    if (result.ToolName != null)
                        sb.AppendLine($"Tool: {result.ToolName}");
                    
                    // Show snippet (highlighted with >>> <<< markers from FTS)
                    sb.AppendLine($"Match: {result.Snippet}");
                    sb.AppendLine();
                }

                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                return $"Error searching memory: {ex.Message}";
            }
        }

        /// <summary>
        /// Rebuild the FTS index from existing messages.
        /// </summary>
        public static async Task<string> RebuildIndexAsync(CancellationToken ct = default)
        {
            try
            {
                var sqlite = SqliteService.Instance;
                if (sqlite == null)
                    return "Error: SQLite service not initialized.";

                await sqlite.RebuildFtsIndexAsync(ct);
                return "FTS index rebuilt successfully. Memory search is now available for all past messages.";
            }
            catch (Exception ex)
            {
                return $"Error rebuilding FTS index: {ex.Message}";
            }
        }
    }
}
