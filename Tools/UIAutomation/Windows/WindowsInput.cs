using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using thuvu.Tools.UIAutomation.Models;
using thuvu.Tools.UIAutomation.Windows.Win32;
using static thuvu.Tools.UIAutomation.Windows.Win32.NativeMethods;
using static thuvu.Tools.UIAutomation.Windows.Win32.Constants;
using static thuvu.Tools.UIAutomation.Windows.Win32.Structs;

namespace thuvu.Tools.UIAutomation.Windows
{
    /// <summary>
    /// Windows implementation for mouse and keyboard input simulation
    /// </summary>
    public static class WindowsInput
    {
        /// <summary>
        /// Move the mouse cursor to absolute screen coordinates
        /// </summary>
        public static bool MoveMouse(int x, int y)
        {
            return SetCursorPos(x, y);
        }
        
        /// <summary>
        /// Get current mouse cursor position
        /// </summary>
        public static (int X, int Y) GetMousePosition()
        {
            if (GetCursorPos(out POINT pt))
                return (pt.X, pt.Y);
            return (0, 0);
        }
        
        /// <summary>
        /// Perform a mouse click at specified coordinates
        /// </summary>
        public static bool Click(int x, int y, string button = "left", int clicks = 1, int delayMs = 50)
        {
            // Move cursor to position
            if (!SetCursorPos(x, y))
                return false;
            
            // Small delay after moving
            Thread.Sleep(10);
            
            // Get button flags
            uint downFlag, upFlag;
            switch (button.ToLowerInvariant())
            {
                case "right":
                    downFlag = MOUSEEVENTF_RIGHTDOWN;
                    upFlag = MOUSEEVENTF_RIGHTUP;
                    break;
                case "middle":
                    downFlag = MOUSEEVENTF_MIDDLEDOWN;
                    upFlag = MOUSEEVENTF_MIDDLEUP;
                    break;
                default: // left
                    downFlag = MOUSEEVENTF_LEFTDOWN;
                    upFlag = MOUSEEVENTF_LEFTUP;
                    break;
            }
            
            // Perform clicks
            for (int i = 0; i < clicks; i++)
            {
                if (i > 0 && delayMs > 0)
                    Thread.Sleep(delayMs);
                
                var inputs = new INPUT[2];
                
                inputs[0].type = INPUT_MOUSE;
                inputs[0].u.mi.dwFlags = downFlag;
                
                inputs[1].type = INPUT_MOUSE;
                inputs[1].u.mi.dwFlags = upFlag;
                
                var sent = SendInput(2, inputs, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
                if (sent != 2)
                    return false;
            }
            
            return true;
        }
        
        /// <summary>
        /// Type text as keyboard input using Unicode
        /// </summary>
        public static bool TypeText(string text, int delayBetweenKeysMs = 10)
        {
            if (string.IsNullOrEmpty(text))
                return true;
            
            foreach (char c in text)
            {
                // Create key down and key up events for each character
                var inputs = new INPUT[2];
                
                inputs[0].type = INPUT_KEYBOARD;
                inputs[0].u.ki.wVk = 0;
                inputs[0].u.ki.wScan = c;
                inputs[0].u.ki.dwFlags = KEYEVENTF_UNICODE;
                inputs[0].u.ki.time = 0;
                inputs[0].u.ki.dwExtraInfo = IntPtr.Zero;
                
                inputs[1].type = INPUT_KEYBOARD;
                inputs[1].u.ki.wVk = 0;
                inputs[1].u.ki.wScan = c;
                inputs[1].u.ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
                inputs[1].u.ki.time = 0;
                inputs[1].u.ki.dwExtraInfo = IntPtr.Zero;
                
                var sent = SendInput(2, inputs, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
                if (sent != 2)
                    return false;
                
                if (delayBetweenKeysMs > 0)
                    Thread.Sleep(delayBetweenKeysMs);
            }
            
            return true;
        }
        
        /// <summary>
        /// Send keyboard shortcut (e.g., Ctrl+S, Alt+F4)
        /// </summary>
        public static bool SendKeys(string[] keys)
        {
            if (keys == null || keys.Length == 0)
                return true;
            
            var vkCodes = new List<ushort>();
            
            // Convert key names to virtual key codes
            foreach (var key in keys)
            {
                var vk = GetVirtualKeyCode(key);
                if (vk == 0)
                {
                    // Try as single character
                    if (key.Length == 1)
                    {
                        vk = (ushort)char.ToUpperInvariant(key[0]);
                    }
                    else
                    {
                        return false; // Unknown key
                    }
                }
                vkCodes.Add(vk);
            }
            
            // Build input array: all keys down, then all keys up (in reverse)
            var inputs = new INPUT[vkCodes.Count * 2];
            int idx = 0;
            
            // Key down events
            foreach (var vk in vkCodes)
            {
                inputs[idx].type = INPUT_KEYBOARD;
                inputs[idx].u.ki.wVk = vk;
                inputs[idx].u.ki.dwFlags = IsExtendedKey(vk) ? KEYEVENTF_EXTENDEDKEY : 0;
                idx++;
            }
            
            // Key up events (reverse order)
            for (int i = vkCodes.Count - 1; i >= 0; i--)
            {
                inputs[idx].type = INPUT_KEYBOARD;
                inputs[idx].u.ki.wVk = vkCodes[i];
                inputs[idx].u.ki.dwFlags = KEYEVENTF_KEYUP | (IsExtendedKey(vkCodes[i]) ? KEYEVENTF_EXTENDEDKEY : 0);
                idx++;
            }
            
            var sent = SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
            return sent == inputs.Length;
        }
        
        /// <summary>
        /// Convert key name to virtual key code
        /// </summary>
        private static ushort GetVirtualKeyCode(string keyName)
        {
            return keyName.ToLowerInvariant() switch
            {
                "ctrl" or "control" => VK_CONTROL,
                "alt" or "menu" => VK_MENU,
                "shift" => VK_SHIFT,
                "win" or "windows" or "lwin" => VK_LWIN,
                "enter" or "return" => VK_RETURN,
                "tab" => VK_TAB,
                "esc" or "escape" => VK_ESCAPE,
                "space" => VK_SPACE,
                "backspace" or "back" => VK_BACK,
                "delete" or "del" => VK_DELETE,
                "left" => VK_LEFT,
                "right" => VK_RIGHT,
                "up" => VK_UP,
                "down" => VK_DOWN,
                "f1" => VK_F1,
                "f2" => VK_F2,
                "f3" => VK_F3,
                "f4" => VK_F4,
                "f5" => VK_F5,
                "f6" => VK_F6,
                "f7" => VK_F7,
                "f8" => VK_F8,
                "f9" => VK_F9,
                "f10" => VK_F10,
                "f11" => VK_F11,
                "f12" => VK_F12,
                // Letters and numbers
                _ when keyName.Length == 1 && char.IsLetterOrDigit(keyName[0]) => 
                    (ushort)char.ToUpperInvariant(keyName[0]),
                _ => 0
            };
        }
        
        /// <summary>
        /// Check if a virtual key code is an extended key
        /// </summary>
        private static bool IsExtendedKey(ushort vk)
        {
            return vk == VK_MENU || vk == VK_CONTROL || 
                   vk == VK_LEFT || vk == VK_RIGHT || vk == VK_UP || vk == VK_DOWN ||
                   vk == VK_DELETE || vk == VK_LWIN;
        }
    }
}
