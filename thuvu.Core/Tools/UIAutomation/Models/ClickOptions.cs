namespace thuvu.Tools.UIAutomation.Models
{
    /// <summary>
    /// Options for mouse click operations
    /// </summary>
    public class ClickOptions
    {
        /// <summary>
        /// Mouse button: "left", "right", or "middle"
        /// </summary>
        public string Button { get; set; } = "left";
        
        /// <summary>
        /// Number of clicks: 1 for single, 2 for double
        /// </summary>
        public int Clicks { get; set; } = 1;
        
        /// <summary>
        /// Delay between clicks in milliseconds
        /// </summary>
        public int DelayMs { get; set; } = 50;
        
        /// <summary>
        /// If true, coordinates are relative to window
        /// </summary>
        public bool WindowRelative { get; set; } = false;
        
        /// <summary>
        /// Target window title for relative coordinates
        /// </summary>
        public string? WindowTitle { get; set; }
    }
}
