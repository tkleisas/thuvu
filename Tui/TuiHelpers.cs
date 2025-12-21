using System;
using Terminal.Gui;
using thuvu.Models;

namespace thuvu.Tui
{
    /// <summary>
    /// Helper methods for updating UI components
    /// </summary>
    public static class TuiHelpers
    {
        /// <summary>
        /// Format elapsed time for display
        /// </summary>
        public static string FormatElapsed(TimeSpan elapsed)
        {
            if (elapsed.TotalMinutes >= 1)
                return $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s";
            return $"{elapsed.TotalSeconds:F1}s";
        }
        
        /// <summary>
        /// Get status icon for tool status
        /// </summary>
        public static string GetStatusIcon(ToolStatus status)
        {
            return status switch
            {
                ToolStatus.Running => "⏳",
                ToolStatus.Completed => "✓",
                ToolStatus.Failed => "✗",
                ToolStatus.TimedOut => "⏱",
                ToolStatus.Cancelled => "⊘",
                _ => "○"
            };
        }
        
        /// <summary>
        /// Thread-safe append to a TextView with auto-scroll
        /// </summary>
        public static void AppendToTextView(TextView view, string text)
        {
            Application.AddTimeout(TimeSpan.Zero, () =>
            {
                try
                {
                    var currentText = view.Text ?? "";
                    view.Text = currentText + text;
                    view.MoveEnd();
                    view.SetNeedsDraw();
                }
                catch { }
                return false;
            });
            Application.Wakeup();
        }
        
        /// <summary>
        /// Thread-safe update of label text
        /// </summary>
        public static void UpdateLabel(Label label, string text)
        {
            Application.AddTimeout(TimeSpan.Zero, () =>
            {
                try
                {
                    label.Text = text;
                    label.SetNeedsDraw();
                }
                catch { }
                return false;
            });
            Application.Wakeup();
        }
        
        /// <summary>
        /// Format tool call result for display
        /// </summary>
        public static string FormatToolResult(string toolName, string result, TimeSpan? elapsed = null)
        {
            var statusIcon = result.Contains("\"error\"") || result.Contains("\"timed_out\":true") ? "[X]" : "[OK]";
            var elapsedStr = elapsed.HasValue ? $" ({FormatElapsed(elapsed.Value)})" : "";
            return $"  TOOL {statusIcon} {toolName}{elapsedStr}";
        }
        
        /// <summary>
        /// Format tool progress for display
        /// </summary>
        public static string FormatToolProgress(ToolProgress progress)
        {
            var icon = GetStatusIcon(progress.Status);
            return $"{icon} {progress.ToolName} {progress.ElapsedFormatted}";
        }
        
        /// <summary>
        /// Get a shortened path for display
        /// </summary>
        public static string ShortenPath(string path, int maxLength = 30)
        {
            if (path.Length <= maxLength) return path;
            return "..." + path.Substring(path.Length - (maxLength - 3));
        }
    }
}
