using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using thuvu.Tools.UIAutomation.Models;
using thuvu.Tools.UIAutomation.Windows.Win32;
using static thuvu.Tools.UIAutomation.Windows.Win32.NativeMethods;
using static thuvu.Tools.UIAutomation.Windows.Win32.Constants;
using static thuvu.Tools.UIAutomation.Windows.Win32.Structs;

namespace thuvu.Tools.UIAutomation.Windows
{
    /// <summary>
    /// Windows implementation for window enumeration and management
    /// </summary>
    public static class WindowsWindowManager
    {
        /// <summary>
        /// Enumerate all top-level windows
        /// </summary>
        public static List<WindowInfo> EnumerateWindows(bool includeHidden = false, string? titleFilter = null)
        {
            var windows = new List<WindowInfo>();
            var foregroundHwnd = GetForegroundWindow();
            
            EnumWindows((hwnd, lParam) =>
            {
                try
                {
                    // Get window info
                    var info = GetWindowInfo(hwnd, foregroundHwnd);
                    
                    // Skip windows without titles unless including hidden
                    if (string.IsNullOrWhiteSpace(info.Title) && !includeHidden)
                        return true;
                    
                    // Filter hidden windows
                    if (!includeHidden && !info.IsVisible)
                        return true;
                    
                    // Filter by title
                    if (!string.IsNullOrEmpty(titleFilter) &&
                        !info.Title.Contains(titleFilter, StringComparison.OrdinalIgnoreCase))
                        return true;
                    
                    windows.Add(info);
                }
                catch
                {
                    // Skip windows that cause errors
                }
                
                return true; // Continue enumeration
            }, IntPtr.Zero);
            
            return windows;
        }
        
        /// <summary>
        /// Find a window by title (partial match)
        /// </summary>
        public static WindowInfo? FindWindow(string titlePattern)
        {
            WindowInfo? found = null;
            var foregroundHwnd = GetForegroundWindow();
            
            EnumWindows((hwnd, lParam) =>
            {
                try
                {
                    var title = GetWindowTitle(hwnd);
                    if (!string.IsNullOrEmpty(title) &&
                        title.Contains(titlePattern, StringComparison.OrdinalIgnoreCase))
                    {
                        found = GetWindowInfo(hwnd, foregroundHwnd);
                        return false; // Stop enumeration
                    }
                }
                catch
                {
                    // Continue on error
                }
                
                return true;
            }, IntPtr.Zero);
            
            return found;
        }
        
        /// <summary>
        /// Get information about a specific window
        /// </summary>
        public static WindowInfo GetWindowInfo(IntPtr hwnd, IntPtr? foregroundHwnd = null)
        {
            foregroundHwnd ??= GetForegroundWindow();
            
            var info = new WindowInfo
            {
                Handle = hwnd,
                Title = GetWindowTitle(hwnd),
                ClassName = GetWindowClassName(hwnd),
                IsVisible = IsWindowVisible(hwnd),
                IsMinimized = IsIconic(hwnd),
                IsMaximized = IsZoomed(hwnd),
                IsForeground = hwnd == foregroundHwnd
            };
            
            // Get window rect
            if (GetWindowRect(hwnd, out RECT rect))
            {
                info.X = rect.Left;
                info.Y = rect.Top;
                info.Width = rect.Width;
                info.Height = rect.Height;
            }
            
            // Get process info
            if (GetWindowThreadProcessId(hwnd, out uint processId) != 0)
            {
                info.ProcessId = (int)processId;
                try
                {
                    using var process = Process.GetProcessById((int)processId);
                    info.ProcessName = process.ProcessName;
                }
                catch
                {
                    info.ProcessName = "Unknown";
                }
            }
            
            return info;
        }
        
        /// <summary>
        /// Get window title text
        /// </summary>
        public static string GetWindowTitle(IntPtr hwnd)
        {
            int length = GetWindowTextLength(hwnd);
            if (length == 0)
                return string.Empty;
            
            var sb = new StringBuilder(length + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }
        
        /// <summary>
        /// Get window class name
        /// </summary>
        public static string GetWindowClassName(IntPtr hwnd)
        {
            var sb = new StringBuilder(256);
            GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }
        
        /// <summary>
        /// Get the foreground (active) window
        /// </summary>
        public static WindowInfo? GetForegroundWindowInfo()
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
                return null;
            
            return GetWindowInfo(hwnd, hwnd);
        }
        
        /// <summary>
        /// Focus a window by handle
        /// </summary>
        public static bool FocusWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return false;
            
            // Restore if minimized
            if (IsIconic(hwnd))
            {
                ShowWindow(hwnd, SW_RESTORE);
            }
            
            // Bring to foreground
            var result = SetForegroundWindow(hwnd);
            
            // Also bring to top
            BringWindowToTop(hwnd);
            
            return result;
        }
        
        /// <summary>
        /// Focus a window by title
        /// </summary>
        public static bool FocusWindow(string titlePattern)
        {
            var window = FindWindow(titlePattern);
            if (window == null)
                return false;
            
            return FocusWindow(window.Handle);
        }
    }
}
