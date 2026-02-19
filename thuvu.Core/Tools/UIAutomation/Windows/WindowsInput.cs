using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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
        /// Input method for keyboard simulation
        /// </summary>
        public enum KeyboardInputMethod
        {
            /// <summary>Use SendInput with virtual key codes (works for most apps)</summary>
            VirtualKey,
            /// <summary>Use SendInput with scan codes (works for DirectInput games)</summary>
            ScanCode,
            /// <summary>Use SendInput with both VK and scan code (maximum compatibility)</summary>
            Both,
            /// <summary>Use Unicode character input (text only, not for games)</summary>
            Unicode
        }
        
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
                
                var sent = SendInput(2, inputs, Marshal.SizeOf<INPUT>());
                if (sent != 2)
                    return false;
            }
            
            return true;
        }
        
        /// <summary>
        /// Type text as keyboard input using Unicode (for text fields, not games)
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
                
                var sent = SendInput(2, inputs, Marshal.SizeOf<INPUT>());
                if (sent != 2)
                    return false;
                
                if (delayBetweenKeysMs > 0)
                    Thread.Sleep(delayBetweenKeysMs);
            }
            
            return true;
        }
        
        /// <summary>
        /// Send keyboard shortcut (e.g., Ctrl+S, Alt+F4) - uses virtual keys by default
        /// </summary>
        public static bool SendKeys(string[] keys, KeyboardInputMethod method = KeyboardInputMethod.VirtualKey)
        {
            if (keys == null || keys.Length == 0)
                return true;
            
            var vkCodes = new List<ushort>();
            var scanCodes = new List<ushort>();
            
            // Convert key names to virtual key codes and scan codes
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
                scanCodes.Add(GetScanCode(vk));
            }
            
            // Build input array based on method
            return method switch
            {
                KeyboardInputMethod.ScanCode => SendKeysWithScanCode(vkCodes, scanCodes),
                KeyboardInputMethod.Both => SendKeysWithBoth(vkCodes, scanCodes),
                _ => SendKeysWithVirtualKey(vkCodes)
            };
        }
        
        /// <summary>
        /// Send a single key press using scan codes (for games using DirectInput/RawInput)
        /// </summary>
        public static bool SendKeyPress(string key, int holdTimeMs = 50, KeyboardInputMethod method = KeyboardInputMethod.ScanCode)
        {
            var vk = GetVirtualKeyCode(key);
            if (vk == 0 && key.Length == 1)
                vk = (ushort)char.ToUpperInvariant(key[0]);
            if (vk == 0)
                return false;
            
            var scanCode = GetScanCode(vk);
            bool extended = IsExtendedKey(vk);
            
            // Key down
            var downInput = new INPUT[1];
            downInput[0].type = INPUT_KEYBOARD;
            
            switch (method)
            {
                case KeyboardInputMethod.ScanCode:
                    downInput[0].u.ki.wVk = 0;
                    downInput[0].u.ki.wScan = scanCode;
                    downInput[0].u.ki.dwFlags = KEYEVENTF_SCANCODE | (extended ? KEYEVENTF_EXTENDEDKEY : 0);
                    break;
                case KeyboardInputMethod.Both:
                    downInput[0].u.ki.wVk = vk;
                    downInput[0].u.ki.wScan = scanCode;
                    downInput[0].u.ki.dwFlags = KEYEVENTF_SCANCODE | (extended ? KEYEVENTF_EXTENDEDKEY : 0);
                    break;
                default: // VirtualKey
                    downInput[0].u.ki.wVk = vk;
                    downInput[0].u.ki.wScan = 0;
                    downInput[0].u.ki.dwFlags = extended ? KEYEVENTF_EXTENDEDKEY : 0;
                    break;
            }
            
            var sent = SendInput(1, downInput, Marshal.SizeOf<INPUT>());
            if (sent != 1)
                return false;
            
            // Hold time
            if (holdTimeMs > 0)
                Thread.Sleep(holdTimeMs);
            
            // Key up
            var upInput = new INPUT[1];
            upInput[0].type = INPUT_KEYBOARD;
            
            switch (method)
            {
                case KeyboardInputMethod.ScanCode:
                    upInput[0].u.ki.wVk = 0;
                    upInput[0].u.ki.wScan = scanCode;
                    upInput[0].u.ki.dwFlags = KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP | (extended ? KEYEVENTF_EXTENDEDKEY : 0);
                    break;
                case KeyboardInputMethod.Both:
                    upInput[0].u.ki.wVk = vk;
                    upInput[0].u.ki.wScan = scanCode;
                    upInput[0].u.ki.dwFlags = KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP | (extended ? KEYEVENTF_EXTENDEDKEY : 0);
                    break;
                default: // VirtualKey
                    upInput[0].u.ki.wVk = vk;
                    upInput[0].u.ki.wScan = 0;
                    upInput[0].u.ki.dwFlags = KEYEVENTF_KEYUP | (extended ? KEYEVENTF_EXTENDEDKEY : 0);
                    break;
            }
            
            sent = SendInput(1, upInput, Marshal.SizeOf<INPUT>());
            return sent == 1;
        }
        
        /// <summary>
        /// Send multiple key presses in sequence (for games)
        /// </summary>
        public static bool SendKeySequence(string[] keys, int delayBetweenKeysMs = 50, int holdTimeMs = 50, KeyboardInputMethod method = KeyboardInputMethod.ScanCode)
        {
            foreach (var key in keys)
            {
                if (!SendKeyPress(key, holdTimeMs, method))
                    return false;
                
                if (delayBetweenKeysMs > 0)
                    Thread.Sleep(delayBetweenKeysMs);
            }
            return true;
        }
        
        /// <summary>
        /// Hold a key down (for continuous input in games)
        /// </summary>
        public static bool KeyDown(string key, KeyboardInputMethod method = KeyboardInputMethod.ScanCode)
        {
            var vk = GetVirtualKeyCode(key);
            if (vk == 0 && key.Length == 1)
                vk = (ushort)char.ToUpperInvariant(key[0]);
            if (vk == 0)
                return false;
            
            var scanCode = GetScanCode(vk);
            bool extended = IsExtendedKey(vk);
            
            var input = new INPUT[1];
            input[0].type = INPUT_KEYBOARD;
            
            switch (method)
            {
                case KeyboardInputMethod.ScanCode:
                    input[0].u.ki.wVk = 0;
                    input[0].u.ki.wScan = scanCode;
                    input[0].u.ki.dwFlags = KEYEVENTF_SCANCODE | (extended ? KEYEVENTF_EXTENDEDKEY : 0);
                    break;
                case KeyboardInputMethod.Both:
                    input[0].u.ki.wVk = vk;
                    input[0].u.ki.wScan = scanCode;
                    input[0].u.ki.dwFlags = KEYEVENTF_SCANCODE | (extended ? KEYEVENTF_EXTENDEDKEY : 0);
                    break;
                default:
                    input[0].u.ki.wVk = vk;
                    input[0].u.ki.wScan = 0;
                    input[0].u.ki.dwFlags = extended ? KEYEVENTF_EXTENDEDKEY : 0;
                    break;
            }
            
            return SendInput(1, input, Marshal.SizeOf<INPUT>()) == 1;
        }
        
        /// <summary>
        /// Release a held key
        /// </summary>
        public static bool KeyUp(string key, KeyboardInputMethod method = KeyboardInputMethod.ScanCode)
        {
            var vk = GetVirtualKeyCode(key);
            if (vk == 0 && key.Length == 1)
                vk = (ushort)char.ToUpperInvariant(key[0]);
            if (vk == 0)
                return false;
            
            var scanCode = GetScanCode(vk);
            bool extended = IsExtendedKey(vk);
            
            var input = new INPUT[1];
            input[0].type = INPUT_KEYBOARD;
            
            switch (method)
            {
                case KeyboardInputMethod.ScanCode:
                    input[0].u.ki.wVk = 0;
                    input[0].u.ki.wScan = scanCode;
                    input[0].u.ki.dwFlags = KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP | (extended ? KEYEVENTF_EXTENDEDKEY : 0);
                    break;
                case KeyboardInputMethod.Both:
                    input[0].u.ki.wVk = vk;
                    input[0].u.ki.wScan = scanCode;
                    input[0].u.ki.dwFlags = KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP | (extended ? KEYEVENTF_EXTENDEDKEY : 0);
                    break;
                default:
                    input[0].u.ki.wVk = vk;
                    input[0].u.ki.wScan = 0;
                    input[0].u.ki.dwFlags = KEYEVENTF_KEYUP | (extended ? KEYEVENTF_EXTENDEDKEY : 0);
                    break;
            }
            
            return SendInput(1, input, Marshal.SizeOf<INPUT>()) == 1;
        }
        
        #region Private helper methods
        
        private static bool SendKeysWithVirtualKey(List<ushort> vkCodes)
        {
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
            
            var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
            return sent == inputs.Length;
        }
        
        private static bool SendKeysWithScanCode(List<ushort> vkCodes, List<ushort> scanCodes)
        {
            var inputs = new INPUT[vkCodes.Count * 2];
            int idx = 0;
            
            // Key down events
            for (int i = 0; i < vkCodes.Count; i++)
            {
                inputs[idx].type = INPUT_KEYBOARD;
                inputs[idx].u.ki.wVk = 0;
                inputs[idx].u.ki.wScan = scanCodes[i];
                inputs[idx].u.ki.dwFlags = KEYEVENTF_SCANCODE | (IsExtendedKey(vkCodes[i]) ? KEYEVENTF_EXTENDEDKEY : 0);
                idx++;
            }
            
            // Key up events (reverse order)
            for (int i = vkCodes.Count - 1; i >= 0; i--)
            {
                inputs[idx].type = INPUT_KEYBOARD;
                inputs[idx].u.ki.wVk = 0;
                inputs[idx].u.ki.wScan = scanCodes[i];
                inputs[idx].u.ki.dwFlags = KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP | (IsExtendedKey(vkCodes[i]) ? KEYEVENTF_EXTENDEDKEY : 0);
                idx++;
            }
            
            var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
            return sent == inputs.Length;
        }
        
        private static bool SendKeysWithBoth(List<ushort> vkCodes, List<ushort> scanCodes)
        {
            var inputs = new INPUT[vkCodes.Count * 2];
            int idx = 0;
            
            // Key down events - include both VK and scan code
            for (int i = 0; i < vkCodes.Count; i++)
            {
                inputs[idx].type = INPUT_KEYBOARD;
                inputs[idx].u.ki.wVk = vkCodes[i];
                inputs[idx].u.ki.wScan = scanCodes[i];
                inputs[idx].u.ki.dwFlags = KEYEVENTF_SCANCODE | (IsExtendedKey(vkCodes[i]) ? KEYEVENTF_EXTENDEDKEY : 0);
                idx++;
            }
            
            // Key up events (reverse order)
            for (int i = vkCodes.Count - 1; i >= 0; i--)
            {
                inputs[idx].type = INPUT_KEYBOARD;
                inputs[idx].u.ki.wVk = vkCodes[i];
                inputs[idx].u.ki.wScan = scanCodes[i];
                inputs[idx].u.ki.dwFlags = KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP | (IsExtendedKey(vkCodes[i]) ? KEYEVENTF_EXTENDEDKEY : 0);
                idx++;
            }
            
            var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
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
                "insert" or "ins" => VK_INSERT,
                "home" => VK_HOME,
                "end" => VK_END,
                "pageup" or "pgup" => VK_PRIOR,
                "pagedown" or "pgdn" => VK_NEXT,
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
                "numpad0" or "num0" => VK_NUMPAD0,
                "numpad1" or "num1" => VK_NUMPAD1,
                "numpad2" or "num2" => VK_NUMPAD2,
                "numpad3" or "num3" => VK_NUMPAD3,
                "numpad4" or "num4" => VK_NUMPAD4,
                "numpad5" or "num5" => VK_NUMPAD5,
                "numpad6" or "num6" => VK_NUMPAD6,
                "numpad7" or "num7" => VK_NUMPAD7,
                "numpad8" or "num8" => VK_NUMPAD8,
                "numpad9" or "num9" => VK_NUMPAD9,
                "multiply" or "numpad*" => VK_MULTIPLY,
                "add" or "numpad+" => VK_ADD,
                "subtract" or "numpad-" => VK_SUBTRACT,
                "decimal" or "numpad." => VK_DECIMAL,
                "divide" or "numpad/" => VK_DIVIDE,
                "capslock" or "caps" => VK_CAPITAL,
                "numlock" => VK_NUMLOCK,
                "scrolllock" or "scroll" => VK_SCROLL,
                "pause" => VK_PAUSE,
                "printscreen" or "prtsc" => VK_SNAPSHOT,
                // Letters and numbers
                _ when keyName.Length == 1 && char.IsLetterOrDigit(keyName[0]) => 
                    (ushort)char.ToUpperInvariant(keyName[0]),
                _ => 0
            };
        }
        
        /// <summary>
        /// Get hardware scan code for a virtual key code
        /// </summary>
        private static ushort GetScanCode(ushort vk)
        {
            // Use MapVirtualKey to convert VK to scan code
            return (ushort)MapVirtualKey(vk, MAPVK_VK_TO_VSC);
        }
        
        /// <summary>
        /// Check if a virtual key code is an extended key
        /// </summary>
        private static bool IsExtendedKey(ushort vk)
        {
            // Extended keys include arrow keys, ins/del/home/end/pgup/pgdn, numpad enter/divide, right ctrl/alt
            return vk == VK_MENU || vk == VK_CONTROL || 
                   vk == VK_LEFT || vk == VK_RIGHT || vk == VK_UP || vk == VK_DOWN ||
                   vk == VK_DELETE || vk == VK_INSERT || vk == VK_HOME || vk == VK_END ||
                   vk == VK_PRIOR || vk == VK_NEXT || vk == VK_LWIN ||
                   vk == VK_DIVIDE || vk == VK_NUMLOCK || vk == VK_SNAPSHOT || vk == VK_PAUSE;
        }
        
        #endregion
    }
}
