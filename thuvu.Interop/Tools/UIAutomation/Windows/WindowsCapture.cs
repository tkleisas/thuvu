using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using thuvu.Tools.UIAutomation.Models;
using thuvu.Tools.UIAutomation.Windows.Win32;
using static thuvu.Tools.UIAutomation.Windows.Win32.NativeMethods;
using static thuvu.Tools.UIAutomation.Windows.Win32.Constants;
using static thuvu.Tools.UIAutomation.Windows.Win32.Structs;

namespace thuvu.Tools.UIAutomation.Windows
{
    /// <summary>
    /// Windows implementation for screen and window capture using GDI+
    /// </summary>
    public static class WindowsCapture
    {
        /// <summary>
        /// Capture the entire virtual screen (all monitors)
        /// </summary>
        public static CaptureResult CaptureFullScreen(CaptureOptions options)
        {
            try
            {
                // Get virtual screen bounds (all monitors)
                int left = GetSystemMetrics(SM_XVIRTUALSCREEN);
                int top = GetSystemMetrics(SM_YVIRTUALSCREEN);
                int width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
                int height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
                
                if (width <= 0 || height <= 0)
                {
                    // Fallback to primary screen
                    width = GetSystemMetrics(SM_CXSCREEN);
                    height = GetSystemMetrics(SM_CYSCREEN);
                    left = 0;
                    top = 0;
                }
                
                return CaptureRegion(left, top, width, height, options);
            }
            catch (Exception ex)
            {
                return new CaptureResult
                {
                    Success = false,
                    Error = $"Failed to capture screen: {ex.Message}"
                };
            }
        }
        
        /// <summary>
        /// Capture a specific window
        /// </summary>
        public static CaptureResult CaptureWindow(IntPtr hwnd, CaptureOptions options)
        {
            try
            {
                if (hwnd == IntPtr.Zero)
                {
                    return new CaptureResult
                    {
                        Success = false,
                        Error = "Invalid window handle"
                    };
                }
                
                // Try to get accurate bounds using DWM (handles Aero glass)
                RECT rect;
                int result = DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, 
                    out rect, Marshal.SizeOf<RECT>());
                
                if (result != 0)
                {
                    // Fallback to GetWindowRect
                    if (!GetWindowRect(hwnd, out rect))
                    {
                        return new CaptureResult
                        {
                            Success = false,
                            Error = "Failed to get window bounds"
                        };
                    }
                }
                
                // Check if window is minimized
                if (IsIconic(hwnd))
                {
                    return new CaptureResult
                    {
                        Success = false,
                        Error = "Cannot capture minimized window. Restore it first with ui_focus_window."
                    };
                }
                
                var captureResult = CaptureRegion(rect.Left, rect.Top, rect.Width, rect.Height, options);
                captureResult.WindowTitle = WindowsWindowManager.GetWindowTitle(hwnd);
                
                return captureResult;
            }
            catch (Exception ex)
            {
                return new CaptureResult
                {
                    Success = false,
                    Error = $"Failed to capture window: {ex.Message}"
                };
            }
        }
        
        /// <summary>
        /// Capture a rectangular region of the screen
        /// </summary>
        public static CaptureResult CaptureRegion(int x, int y, int width, int height, CaptureOptions options)
        {
            try
            {
                if (width <= 0 || height <= 0)
                {
                    return new CaptureResult
                    {
                        Success = false,
                        Error = $"Invalid dimensions: {width}x{height}"
                    };
                }
                
                using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                using var graphics = Graphics.FromImage(bitmap);
                
                // Capture screen region
                graphics.CopyFromScreen(x, y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
                
                // Optionally draw cursor
                if (options.IncludeCursor)
                {
                    DrawCursor(graphics, x, y);
                }
                
                return ConvertToResult(bitmap, options);
            }
            catch (Exception ex)
            {
                return new CaptureResult
                {
                    Success = false,
                    Error = $"Failed to capture region: {ex.Message}"
                };
            }
        }
        
        /// <summary>
        /// Draw the cursor onto the captured image
        /// </summary>
        private static void DrawCursor(Graphics graphics, int offsetX, int offsetY)
        {
            try
            {
                var cursorInfo = new CURSORINFO();
                cursorInfo.cbSize = Marshal.SizeOf<CURSORINFO>();
                
                if (GetCursorInfo(ref cursorInfo) && (cursorInfo.flags & CURSOR_SHOWING) != 0)
                {
                    // Get cursor position relative to capture area
                    int cursorX = cursorInfo.ptScreenPos.X - offsetX;
                    int cursorY = cursorInfo.ptScreenPos.Y - offsetY;
                    
                    // Get cursor hotspot
                    if (GetIconInfo(cursorInfo.hCursor, out ICONINFO iconInfo))
                    {
                        try
                        {
                            cursorX -= iconInfo.xHotspot;
                            cursorY -= iconInfo.yHotspot;
                            
                            // Draw cursor
                            var hdc = graphics.GetHdc();
                            try
                            {
                                DrawIconEx(hdc, cursorX, cursorY, cursorInfo.hCursor, 
                                    0, 0, 0, IntPtr.Zero, DI_NORMAL);
                            }
                            finally
                            {
                                graphics.ReleaseHdc(hdc);
                            }
                        }
                        finally
                        {
                            if (iconInfo.hbmMask != IntPtr.Zero)
                                DeleteObject(iconInfo.hbmMask);
                            if (iconInfo.hbmColor != IntPtr.Zero)
                                DeleteObject(iconInfo.hbmColor);
                        }
                    }
                }
            }
            catch
            {
                // Ignore cursor drawing errors
            }
        }
        
        /// <summary>
        /// Convert bitmap to result with proper format and encoding
        /// </summary>
        private static CaptureResult ConvertToResult(Bitmap bitmap, CaptureOptions options)
        {
            using var ms = new MemoryStream();
            
            var isJpeg = options.Format.Equals("jpeg", StringComparison.OrdinalIgnoreCase) ||
                         options.Format.Equals("jpg", StringComparison.OrdinalIgnoreCase);
            
            var mimeType = isJpeg ? "image/jpeg" : "image/png";
            
            if (isJpeg)
            {
                // Save as JPEG with quality setting
                var encoder = GetEncoder(ImageFormat.Jpeg);
                if (encoder != null)
                {
                    var encoderParams = new EncoderParameters(1);
                    encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, options.JpegQuality);
                    bitmap.Save(ms, encoder, encoderParams);
                }
                else
                {
                    bitmap.Save(ms, ImageFormat.Jpeg);
                }
            }
            else
            {
                bitmap.Save(ms, ImageFormat.Png);
            }
            
            var result = new CaptureResult
            {
                Success = true,
                Width = bitmap.Width,
                Height = bitmap.Height,
                MimeType = mimeType,
                FileSizeBytes = ms.Length
            };
            
            if (options.Output.Equals("file", StringComparison.OrdinalIgnoreCase) && 
                !string.IsNullOrEmpty(options.FilePath))
            {
                // Ensure directory exists
                var dir = Path.GetDirectoryName(options.FilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                
                File.WriteAllBytes(options.FilePath, ms.ToArray());
                result.FilePath = Path.GetFullPath(options.FilePath);
            }
            else
            {
                result.Base64Data = Convert.ToBase64String(ms.ToArray());
            }
            
            return result;
        }
        
        /// <summary>
        /// Get image encoder for specified format
        /// </summary>
        private static ImageCodecInfo? GetEncoder(ImageFormat format)
        {
            var codecs = ImageCodecInfo.GetImageEncoders();
            foreach (var codec in codecs)
            {
                if (codec.FormatID == format.Guid)
                    return codec;
            }
            return null;
        }
    }
}
