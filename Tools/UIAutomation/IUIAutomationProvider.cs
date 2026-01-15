using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using thuvu.Tools.UIAutomation.Models;

namespace thuvu.Tools.UIAutomation
{
    /// <summary>
    /// Cross-platform interface for UI automation operations.
    /// Implementations exist for Windows, with Linux and macOS planned.
    /// </summary>
    public interface IUIAutomationProvider : IDisposable
    {
        /// <summary>
        /// Platform name (e.g., "Windows", "Linux", "macOS")
        /// </summary>
        string PlatformName { get; }
        
        /// <summary>
        /// Whether this provider is supported on the current platform
        /// </summary>
        bool IsSupported { get; }
        
        #region Screen Capture
        
        /// <summary>
        /// Capture the entire screen (all monitors)
        /// </summary>
        Task<CaptureResult> CaptureScreenAsync(CaptureOptions options);
        
        /// <summary>
        /// Capture a specific window by title
        /// </summary>
        Task<CaptureResult> CaptureWindowAsync(string windowTitle, CaptureOptions options);
        
        /// <summary>
        /// Capture a specific window by handle
        /// </summary>
        Task<CaptureResult> CaptureWindowAsync(IntPtr windowHandle, CaptureOptions options);
        
        /// <summary>
        /// Capture a rectangular region of the screen
        /// </summary>
        Task<CaptureResult> CaptureRegionAsync(int x, int y, int width, int height, CaptureOptions options);
        
        #endregion
        
        #region Window Management
        
        /// <summary>
        /// List all open windows
        /// </summary>
        /// <param name="includeHidden">Include hidden/background windows</param>
        /// <param name="titleFilter">Optional filter by title (case-insensitive partial match)</param>
        Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(bool includeHidden = false, string? titleFilter = null);
        
        /// <summary>
        /// Get information about the currently active window
        /// </summary>
        Task<WindowInfo?> GetActiveWindowAsync();
        
        /// <summary>
        /// Bring a window to the foreground by title
        /// </summary>
        Task<bool> FocusWindowAsync(string windowTitle);
        
        /// <summary>
        /// Bring a window to the foreground by handle
        /// </summary>
        Task<bool> FocusWindowAsync(IntPtr windowHandle);
        
        #endregion
        
        #region Mouse Input (Phase 2)
        
        /// <summary>
        /// Perform a mouse click at the specified coordinates
        /// </summary>
        Task<bool> ClickAsync(int x, int y, ClickOptions? options = null);
        
        /// <summary>
        /// Perform a double-click at the specified coordinates
        /// </summary>
        Task<bool> DoubleClickAsync(int x, int y, ClickOptions? options = null);
        
        /// <summary>
        /// Perform a right-click at the specified coordinates
        /// </summary>
        Task<bool> RightClickAsync(int x, int y, ClickOptions? options = null);
        
        /// <summary>
        /// Move the mouse cursor to the specified coordinates
        /// </summary>
        Task<bool> MoveMouseAsync(int x, int y);
        
        /// <summary>
        /// Get the current mouse cursor position
        /// </summary>
        Task<(int X, int Y)> GetMousePositionAsync();
        
        #endregion
        
        #region Keyboard Input (Phase 2)
        
        /// <summary>
        /// Type text as keyboard input
        /// </summary>
        Task<bool> TypeTextAsync(string text, TypeOptions? options = null);
        
        /// <summary>
        /// Send keyboard shortcuts (e.g., Ctrl+S)
        /// </summary>
        Task<bool> SendKeysAsync(string[] keys, TypeOptions? options = null);
        
        #endregion
        
        #region UI Element Inspection (Phase 2)
        
        /// <summary>
        /// Get UI element at specified coordinates
        /// </summary>
        Task<UIElement?> GetElementAtAsync(int x, int y);
        
        /// <summary>
        /// Find UI elements by selector
        /// </summary>
        Task<IReadOnlyList<UIElement>> FindElementsAsync(string selector);
        
        /// <summary>
        /// Get the currently focused UI element
        /// </summary>
        Task<UIElement?> GetFocusedElementAsync();
        
        #endregion
    }
}
