using System;
using System.Runtime.InteropServices;

namespace thuvu.Tools.UIAutomation.Windows.Win32
{
    /// <summary>
    /// Win32 constant values
    /// </summary>
    public static class Constants
    {
        // GetSystemMetrics constants
        public const int SM_XVIRTUALSCREEN = 76;
        public const int SM_YVIRTUALSCREEN = 77;
        public const int SM_CXVIRTUALSCREEN = 78;
        public const int SM_CYVIRTUALSCREEN = 79;
        public const int SM_CXSCREEN = 0;
        public const int SM_CYSCREEN = 1;
        
        // Window styles
        public const int GWL_STYLE = -16;
        public const int GWL_EXSTYLE = -20;
        public const uint WS_VISIBLE = 0x10000000;
        public const uint WS_MINIMIZE = 0x20000000;
        public const uint WS_MAXIMIZE = 0x01000000;
        
        // ShowWindow commands
        public const int SW_RESTORE = 9;
        public const int SW_SHOW = 5;
        public const int SW_SHOWNOACTIVATE = 4;
        public const int SW_MINIMIZE = 6;
        public const int SW_MAXIMIZE = 3;
        
        // SendInput constants
        public const uint INPUT_MOUSE = 0;
        public const uint INPUT_KEYBOARD = 1;
        
        // Mouse event flags
        public const uint MOUSEEVENTF_MOVE = 0x0001;
        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const uint MOUSEEVENTF_LEFTUP = 0x0004;
        public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        public const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
        public const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
        
        // Keyboard event flags
        public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const uint KEYEVENTF_UNICODE = 0x0004;
        public const uint KEYEVENTF_SCANCODE = 0x0008;
        
        // MapVirtualKey constants
        public const uint MAPVK_VK_TO_VSC = 0;
        public const uint MAPVK_VSC_TO_VK = 1;
        public const uint MAPVK_VK_TO_CHAR = 2;
        public const uint MAPVK_VSC_TO_VK_EX = 3;
        public const uint MAPVK_VK_TO_VSC_EX = 4;
        
        // Virtual key codes
        public const ushort VK_BACK = 0x08;
        public const ushort VK_TAB = 0x09;
        public const ushort VK_RETURN = 0x0D;
        public const ushort VK_SHIFT = 0x10;
        public const ushort VK_CONTROL = 0x11;
        public const ushort VK_MENU = 0x12; // Alt
        public const ushort VK_PAUSE = 0x13;
        public const ushort VK_CAPITAL = 0x14; // Caps Lock
        public const ushort VK_ESCAPE = 0x1B;
        public const ushort VK_SPACE = 0x20;
        public const ushort VK_PRIOR = 0x21; // Page Up
        public const ushort VK_NEXT = 0x22;  // Page Down
        public const ushort VK_END = 0x23;
        public const ushort VK_HOME = 0x24;
        public const ushort VK_LEFT = 0x25;
        public const ushort VK_UP = 0x26;
        public const ushort VK_RIGHT = 0x27;
        public const ushort VK_DOWN = 0x28;
        public const ushort VK_SNAPSHOT = 0x2C; // Print Screen
        public const ushort VK_INSERT = 0x2D;
        public const ushort VK_DELETE = 0x2E;
        public const ushort VK_LWIN = 0x5B;
        public const ushort VK_NUMPAD0 = 0x60;
        public const ushort VK_NUMPAD1 = 0x61;
        public const ushort VK_NUMPAD2 = 0x62;
        public const ushort VK_NUMPAD3 = 0x63;
        public const ushort VK_NUMPAD4 = 0x64;
        public const ushort VK_NUMPAD5 = 0x65;
        public const ushort VK_NUMPAD6 = 0x66;
        public const ushort VK_NUMPAD7 = 0x67;
        public const ushort VK_NUMPAD8 = 0x68;
        public const ushort VK_NUMPAD9 = 0x69;
        public const ushort VK_MULTIPLY = 0x6A;
        public const ushort VK_ADD = 0x6B;
        public const ushort VK_SUBTRACT = 0x6D;
        public const ushort VK_DECIMAL = 0x6E;
        public const ushort VK_DIVIDE = 0x6F;
        public const ushort VK_F1 = 0x70;
        public const ushort VK_F2 = 0x71;
        public const ushort VK_F3 = 0x72;
        public const ushort VK_F4 = 0x73;
        public const ushort VK_F5 = 0x74;
        public const ushort VK_F6 = 0x75;
        public const ushort VK_F7 = 0x76;
        public const ushort VK_F8 = 0x77;
        public const ushort VK_F9 = 0x78;
        public const ushort VK_F10 = 0x79;
        public const ushort VK_F11 = 0x7A;
        public const ushort VK_F12 = 0x7B;
        public const ushort VK_NUMLOCK = 0x90;
        public const ushort VK_SCROLL = 0x91;
        
        // Cursor constants
        public const int CURSOR_SHOWING = 0x00000001;
        
        // CopyPixelOperation
        public const int SRCCOPY = 0x00CC0020;
        public const int CAPTUREBLT = 0x40000000;
        
        // DWM constants
        public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    }
}
