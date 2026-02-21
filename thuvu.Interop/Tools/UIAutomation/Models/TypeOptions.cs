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
        
        /// <summary>
        /// Use hardware scan codes instead of virtual keys.
        /// Enable this for games that use DirectInput/RawInput.
        /// </summary>
        public bool UseScanCodes { get; set; } = false;
        
        /// <summary>
        /// How long to hold each key down in milliseconds (for games)
        /// </summary>
        public int HoldTimeMs { get; set; } = 50;
    }
}
