using System;
using System.Runtime.InteropServices;

namespace thuvu.Tools.UIAutomation
{
    /// <summary>
    /// Factory for creating platform-specific UI automation providers
    /// </summary>
    public static class UIAutomationFactory
    {
        /// <summary>
        /// Create the appropriate UI automation provider for the current platform
        /// </summary>
        public static IUIAutomationProvider Create()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return new Windows.WindowsUIProvider();
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                throw new PlatformNotSupportedException(
                    "Linux UI automation is not yet implemented. Planned for Phase 3.");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                throw new PlatformNotSupportedException(
                    "macOS UI automation is not yet implemented. Planned for Phase 3.");
            }
            else
            {
                throw new PlatformNotSupportedException(
                    $"UI automation is not supported on this platform: {RuntimeInformation.OSDescription}");
            }
        }
        
        /// <summary>
        /// Check if UI automation is supported on the current platform
        /// </summary>
        public static bool IsSupported()
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        }
        
        /// <summary>
        /// Get the name of the current platform
        /// </summary>
        public static string GetPlatformName()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return "Windows";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return "Linux";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return "macOS";
            return "Unknown";
        }
    }
}
