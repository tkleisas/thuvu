using System;
using Terminal.Gui.Views;
using Terminal.Gui.App;
using thuvu.Models;

namespace thuvu.Tui
{
    public static class TuiHelpers
    {
        public static string FormatElapsed(TimeSpan elapsed)
        {
            if (elapsed.TotalMinutes >= 1)
                return $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s";
            return $"{elapsed.TotalSeconds:F1}s";
        }
        
        public static string GetStatusIcon(ToolStatus status)
        {
            return status switch
            {
                ToolStatus.Running => "[...]",
                ToolStatus.Completed => "[OK]",
                ToolStatus.Failed => "[XX]",
                ToolStatus.TimedOut => "[T/O]",
                ToolStatus.Cancelled => "[CAN]",
                _ => "[--]"
            };
        }
        
        public static void AppendToTextView(TextView view, string text)
        {
            Application.Invoke(() =>
            {
                try
                {
                    var currentText = view.Text ?? "";
                    view.Text = currentText + text;
                    view.MoveEnd();
                    view.SetNeedsDraw();
                }
                catch { }
            });
        }
        
        public static void UpdateLabel(Label label, string text)
        {
            Application.Invoke(() =>
            {
                try
                {
                    label.Text = text;
                    label.SetNeedsDraw();
                }
                catch { }
            });
        }
        
        public static string FormatToolResult(string toolName, string result, TimeSpan? elapsed = null)
        {
            var statusIcon = result.Contains("\"error\"") || result.Contains("\"timed_out\":true") ? "[XX]" : "[OK]";
            var elapsedStr = elapsed.HasValue ? $" ({FormatElapsed(elapsed.Value)})" : "";
            return $"  TOOL {statusIcon} {toolName}{elapsedStr}";
        }
        
        public static string FormatToolProgress(ToolProgress progress)
        {
            var icon = GetStatusIcon(progress.Status);
            return $"{icon} {progress.ToolName} {progress.ElapsedFormatted}";
        }
        
        public static string ShortenPath(string path, int maxLength = 30)
        {
            if (path.Length <= maxLength) return path;
            return "..." + path.Substring(path.Length - (maxLength - 3));
        }
    }
}
