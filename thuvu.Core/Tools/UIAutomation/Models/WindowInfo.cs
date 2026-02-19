using System;

namespace thuvu.Tools.UIAutomation.Models
{
    /// <summary>
    /// Information about an open window
    /// </summary>
    public class WindowInfo
    {
        /// <summary>
        /// Native window handle
        /// </summary>
        public IntPtr Handle { get; set; }
        
        /// <summary>
        /// Window title text
        /// </summary>
        public string Title { get; set; } = "";
        
        /// <summary>
        /// Name of the process that owns this window
        /// </summary>
        public string ProcessName { get; set; } = "";
        
        /// <summary>
        /// Process ID of the owning process
        /// </summary>
        public int ProcessId { get; set; }
        
        /// <summary>
        /// Window left position (screen coordinates)
        /// </summary>
        public int X { get; set; }
        
        /// <summary>
        /// Window top position (screen coordinates)
        /// </summary>
        public int Y { get; set; }
        
        /// <summary>
        /// Window width in pixels
        /// </summary>
        public int Width { get; set; }
        
        /// <summary>
        /// Window height in pixels
        /// </summary>
        public int Height { get; set; }
        
        /// <summary>
        /// Whether the window is visible
        /// </summary>
        public bool IsVisible { get; set; }
        
        /// <summary>
        /// Whether the window is minimized
        /// </summary>
        public bool IsMinimized { get; set; }
        
        /// <summary>
        /// Whether the window is maximized
        /// </summary>
        public bool IsMaximized { get; set; }
        
        /// <summary>
        /// Whether this is the foreground (active) window
        /// </summary>
        public bool IsForeground { get; set; }
        
        /// <summary>
        /// Window class name
        /// </summary>
        public string ClassName { get; set; } = "";
    }
}
