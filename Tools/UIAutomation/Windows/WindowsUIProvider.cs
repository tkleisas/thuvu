using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using thuvu.Tools.UIAutomation.Models;

namespace thuvu.Tools.UIAutomation.Windows
{
    /// <summary>
    /// Windows implementation of IUIAutomationProvider
    /// </summary>
    public class WindowsUIProvider : IUIAutomationProvider
    {
        private WindowsUIAutomation? _uiAutomation;
        
        /// <summary>
        /// Get or create the UI Automation instance (lazy initialization)
        /// </summary>
        private WindowsUIAutomation GetUIAutomation()
        {
            _uiAutomation ??= new WindowsUIAutomation();
            return _uiAutomation;
        }
        
        public string PlatformName => "Windows";
        
        public bool IsSupported => true;
        
        #region Screen Capture
        
        public Task<CaptureResult> CaptureScreenAsync(CaptureOptions options)
        {
            return Task.Run(() => WindowsCapture.CaptureFullScreen(options));
        }
        
        public Task<CaptureResult> CaptureWindowAsync(string windowTitle, CaptureOptions options)
        {
            return Task.Run(() =>
            {
                var window = WindowsWindowManager.FindWindow(windowTitle);
                if (window == null)
                {
                    return new CaptureResult
                    {
                        Success = false,
                        Error = $"Window not found: '{windowTitle}'"
                    };
                }
                
                return WindowsCapture.CaptureWindow(window.Handle, options);
            });
        }
        
        public Task<CaptureResult> CaptureWindowAsync(IntPtr windowHandle, CaptureOptions options)
        {
            return Task.Run(() => WindowsCapture.CaptureWindow(windowHandle, options));
        }
        
        public Task<CaptureResult> CaptureRegionAsync(int x, int y, int width, int height, CaptureOptions options)
        {
            return Task.Run(() => WindowsCapture.CaptureRegion(x, y, width, height, options));
        }
        
        #endregion
        
        #region Window Management
        
        public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(bool includeHidden = false, string? titleFilter = null)
        {
            return Task.Run<IReadOnlyList<WindowInfo>>(() => 
                WindowsWindowManager.EnumerateWindows(includeHidden, titleFilter));
        }
        
        public Task<WindowInfo?> GetActiveWindowAsync()
        {
            return Task.Run(() => WindowsWindowManager.GetForegroundWindowInfo());
        }
        
        public Task<bool> FocusWindowAsync(string windowTitle)
        {
            return Task.Run(() => WindowsWindowManager.FocusWindow(windowTitle));
        }
        
        public Task<bool> FocusWindowAsync(IntPtr windowHandle)
        {
            return Task.Run(() => WindowsWindowManager.FocusWindow(windowHandle));
        }
        
        #endregion
        
        #region Mouse Input
        
        public Task<bool> ClickAsync(int x, int y, ClickOptions? options = null)
        {
            return Task.Run(() =>
            {
                options ??= new ClickOptions();
                
                // Convert to absolute coordinates if window-relative
                int absX = x, absY = y;
                if (options.WindowRelative && !string.IsNullOrEmpty(options.WindowTitle))
                {
                    var window = WindowsWindowManager.FindWindow(options.WindowTitle);
                    if (window == null)
                        return false;
                    absX = window.X + x;
                    absY = window.Y + y;
                }
                
                return WindowsInput.Click(absX, absY, options.Button, options.Clicks, options.DelayMs);
            });
        }
        
        public Task<bool> DoubleClickAsync(int x, int y, ClickOptions? options = null)
        {
            options ??= new ClickOptions();
            options.Clicks = 2;
            return ClickAsync(x, y, options);
        }
        
        public Task<bool> RightClickAsync(int x, int y, ClickOptions? options = null)
        {
            options ??= new ClickOptions();
            options.Button = "right";
            return ClickAsync(x, y, options);
        }
        
        public Task<bool> MoveMouseAsync(int x, int y)
        {
            return Task.Run(() => WindowsInput.MoveMouse(x, y));
        }
        
        public Task<(int X, int Y)> GetMousePositionAsync()
        {
            return Task.Run(() => WindowsInput.GetMousePosition());
        }
        
        #endregion
        
        #region Keyboard Input
        
        public Task<bool> TypeTextAsync(string text, TypeOptions? options = null)
        {
            return Task.Run(() =>
            {
                options ??= new TypeOptions();
                
                // Focus target window if specified
                if (!string.IsNullOrEmpty(options.WindowTitle))
                {
                    if (!WindowsWindowManager.FocusWindow(options.WindowTitle))
                        return false;
                    
                    // Small delay after focusing
                    System.Threading.Thread.Sleep(100);
                }
                
                return WindowsInput.TypeText(text, options.DelayBetweenKeysMs);
            });
        }
        
        public Task<bool> SendKeysAsync(string[] keys, TypeOptions? options = null)
        {
            return Task.Run(() =>
            {
                options ??= new TypeOptions();
                
                // Focus target window if specified
                if (!string.IsNullOrEmpty(options.WindowTitle))
                {
                    if (!WindowsWindowManager.FocusWindow(options.WindowTitle))
                        return false;
                    
                    // Small delay after focusing
                    System.Threading.Thread.Sleep(100);
                }
                
                // Use scan code method for games, virtual key for regular apps
                var method = options.UseScanCodes 
                    ? WindowsInput.KeyboardInputMethod.ScanCode 
                    : WindowsInput.KeyboardInputMethod.VirtualKey;
                
                return WindowsInput.SendKeys(keys, method);
            });
        }
        
        /// <summary>
        /// Send a single key press with configurable hold time (for games)
        /// </summary>
        public Task<bool> SendKeyPressAsync(string key, int holdTimeMs = 50, bool useScanCodes = true)
        {
            return Task.Run(() =>
            {
                var method = useScanCodes 
                    ? WindowsInput.KeyboardInputMethod.ScanCode 
                    : WindowsInput.KeyboardInputMethod.VirtualKey;
                return WindowsInput.SendKeyPress(key, holdTimeMs, method);
            });
        }
        
        /// <summary>
        /// Send a sequence of key presses (for games)
        /// </summary>
        public Task<bool> SendKeySequenceAsync(string[] keys, int delayBetweenKeysMs = 50, int holdTimeMs = 50, bool useScanCodes = true)
        {
            return Task.Run(() =>
            {
                var method = useScanCodes 
                    ? WindowsInput.KeyboardInputMethod.ScanCode 
                    : WindowsInput.KeyboardInputMethod.VirtualKey;
                return WindowsInput.SendKeySequence(keys, delayBetweenKeysMs, holdTimeMs, method);
            });
        }
        
        /// <summary>
        /// Hold a key down (for continuous game input)
        /// </summary>
        public Task<bool> KeyDownAsync(string key, bool useScanCodes = true)
        {
            return Task.Run(() =>
            {
                var method = useScanCodes 
                    ? WindowsInput.KeyboardInputMethod.ScanCode 
                    : WindowsInput.KeyboardInputMethod.VirtualKey;
                return WindowsInput.KeyDown(key, method);
            });
        }
        
        /// <summary>
        /// Release a held key
        /// </summary>
        public Task<bool> KeyUpAsync(string key, bool useScanCodes = true)
        {
            return Task.Run(() =>
            {
                var method = useScanCodes 
                    ? WindowsInput.KeyboardInputMethod.ScanCode 
                    : WindowsInput.KeyboardInputMethod.VirtualKey;
                return WindowsInput.KeyUp(key, method);
            });
        }
        
        #endregion
        
        #region UI Element Inspection
        
        public Task<UIElement?> GetElementAtAsync(int x, int y)
        {
            return Task.Run(() => GetUIAutomation().GetElementAt(x, y));
        }
        
        public Task<IReadOnlyList<UIElement>> FindElementsAsync(string selector)
        {
            return Task.Run<IReadOnlyList<UIElement>>(() => GetUIAutomation().FindElements(selector));
        }
        
        public Task<IReadOnlyList<UIElement>> FindElementsAsync(string selector, string? windowTitle)
        {
            return Task.Run<IReadOnlyList<UIElement>>(() => GetUIAutomation().FindElements(selector, windowTitle));
        }
        
        public Task<UIElement?> GetFocusedElementAsync()
        {
            return Task.Run(() => GetUIAutomation().GetFocusedElement());
        }
        
        /// <summary>
        /// Wait for an element to appear
        /// </summary>
        public Task<UIElement?> WaitForElementAsync(string selector, string? windowTitle, int timeoutMs)
        {
            return Task.Run(() => GetUIAutomation().WaitForElement(selector, windowTitle, timeoutMs));
        }
        
        /// <summary>
        /// Wait for a window to appear
        /// </summary>
        public Task<bool> WaitForWindowAsync(string titlePattern, int timeoutMs)
        {
            return Task.Run(() => GetUIAutomation().WaitForWindow(titlePattern, timeoutMs));
        }
        
        /// <summary>
        /// Get element tree for a window
        /// </summary>
        public Task<UIElement?> GetWindowElementTreeAsync(string windowTitle, int maxDepth = 3)
        {
            return Task.Run(() => GetUIAutomation().GetWindowElementTree(windowTitle, maxDepth));
        }
        
        #endregion
        
        public void Dispose()
        {
            _uiAutomation?.Dispose();
            _uiAutomation = null;
        }
    }
}
