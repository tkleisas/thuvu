namespace thuvu.Tools.UIAutomation.Models
{
    /// <summary>
    /// Options for screen/window capture
    /// </summary>
    public class CaptureOptions
    {
        /// <summary>
        /// Image format: "png" or "jpeg"
        /// </summary>
        public string Format { get; set; } = "png";
        
        /// <summary>
        /// JPEG quality (1-100), ignored for PNG
        /// </summary>
        public int JpegQuality { get; set; } = 85;
        
        /// <summary>
        /// Output mode: "base64" or "file"
        /// </summary>
        public string Output { get; set; } = "base64";
        
        /// <summary>
        /// File path for saving (when output is "file")
        /// </summary>
        public string? FilePath { get; set; }
        
        /// <summary>
        /// Whether to include the cursor in the capture
        /// </summary>
        public bool IncludeCursor { get; set; } = false;
    }
}
