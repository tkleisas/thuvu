namespace thuvu.Tools.UIAutomation.Models
{
    /// <summary>
    /// Options for keyboard input operations
    /// </summary>
    public class TypeOptions
    {
        /// <summary>
        /// Delay between keystrokes in milliseconds
        /// </summary>
        public int DelayBetweenKeysMs { get; set; } = 10;
        
        /// <summary>
        /// If true, sends input to the currently active window
        /// </summary>
        public bool SendToActiveWindow { get; set; } = true;
        
        /// <summary>
        /// Target window title (will focus before typing)
        /// </summary>
        public string? WindowTitle { get; set; }
    }
}
