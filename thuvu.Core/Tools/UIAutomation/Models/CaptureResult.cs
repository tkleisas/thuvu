namespace thuvu.Tools.UIAutomation.Models
{
    /// <summary>
    /// Result of a screen/window capture operation
    /// </summary>
    public class CaptureResult
    {
        /// <summary>
        /// Whether the capture succeeded
        /// </summary>
        public bool Success { get; set; }
        
        /// <summary>
        /// Error message if capture failed
        /// </summary>
        public string? Error { get; set; }
        
        /// <summary>
        /// Base64-encoded image data (if output mode is base64)
        /// </summary>
        public string? Base64Data { get; set; }
        
        /// <summary>
        /// File path where image was saved (if output mode is file)
        /// </summary>
        public string? FilePath { get; set; }
        
        /// <summary>
        /// MIME type of the image (image/png or image/jpeg)
        /// </summary>
        public string MimeType { get; set; } = "image/png";
        
        /// <summary>
        /// Image width in pixels
        /// </summary>
        public int Width { get; set; }
        
        /// <summary>
        /// Image height in pixels
        /// </summary>
        public int Height { get; set; }
        
        /// <summary>
        /// Size of the image data in bytes
        /// </summary>
        public long FileSizeBytes { get; set; }
        
        /// <summary>
        /// Window title if capturing a specific window
        /// </summary>
        public string? WindowTitle { get; set; }
    }
}
