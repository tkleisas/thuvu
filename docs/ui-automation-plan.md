# UI Automation Module - Implementation Plan

## T.H.U.V.U. Screen Capture & UI Automation Feature

**Version:** 1.0  
**Date:** 2025-01-15  
**Author:** Agent  
**Status:** Phase 2 Complete

---

## 1. Executive Summary

This document outlines the implementation plan for adding UI automation capabilities to THUVU, enabling the agent to:
- Capture screenshots of screens, windows, and regions
- List and enumerate open windows
- Interact with UI elements (click, type, move mouse)
- Inspect UI element trees for intelligent automation

The primary use case is **autonomous debugging** - allowing the agent to run applications, observe their visual state, and interact with them programmatically.

---

## 2. Architecture Overview

### 2.1 Cross-Platform Abstraction

```
┌─────────────────────────────────────────────────────────────────┐
│                         THUVU Agent                              │
│                    (ToolExecutor.cs)                             │
├─────────────────────────────────────────────────────────────────┤
│                   IUIAutomationProvider                          │
│  Interface defining all UI automation operations                 │
└─────────────────────────────────────────────────────────────────┘
                              │
            ┌─────────────────┼─────────────────┐
            ▼                 ▼                 ▼
    ┌───────────────┐ ┌───────────────┐ ┌───────────────┐
    │   Windows     │ │    Linux      │ │    macOS      │
    │   Provider    │ │   Provider    │ │   Provider    │
    │  (Phase 1)    │ │  (Phase 3)    │ │  (Phase 3)    │
    ├───────────────┤ ├───────────────┤ ├───────────────┤
    │ • Win32 API   │ │ • X11/XCB     │ │ • AppKit      │
    │ • GDI+        │ │ • libxdo      │ │ • CoreGraphics│
    │ • UI Auto     │ │ • AT-SPI      │ │ • Accessibility│
    └───────────────┘ └───────────────┘ └───────────────┘
```

### 2.2 File Structure

```
Tools/
├── UIAutomation/
│   ├── IUIAutomationProvider.cs      # Cross-platform interface
│   ├── UIAutomationFactory.cs        # Platform detection & instantiation
│   ├── UIAutomationToolImpl.cs       # Tool implementation (entry point)
│   │
│   ├── Windows/
│   │   ├── WindowsUIProvider.cs      # Main Windows implementation
│   │   ├── WindowsCapture.cs         # Screen/window capture logic
│   │   ├── WindowsInput.cs           # Mouse/keyboard simulation
│   │   ├── WindowsWindowManager.cs   # Window enumeration
│   │   └── Win32/
│   │       ├── NativeMethods.cs      # P/Invoke declarations
│   │       ├── Structs.cs            # Native struct definitions
│   │       └── Constants.cs          # Win32 constants
│   │
│   ├── Linux/                        # Future: Phase 3
│   │   └── LinuxUIProvider.cs
│   │
│   ├── MacOS/                        # Future: Phase 3
│   │   └── MacOSUIProvider.cs
│   │
│   └── Models/
│       ├── WindowInfo.cs             # Window metadata
│       ├── CaptureResult.cs          # Screenshot result
│       ├── UIElement.cs              # UI element for tree inspection
│       ├── ClickOptions.cs           # Mouse click parameters
│       ├── TypeOptions.cs            # Keyboard input parameters
│       └── CaptureOptions.cs         # Screenshot parameters
```

---

## 3. Interface Design

### 3.1 IUIAutomationProvider Interface

```csharp
namespace thuvu.Tools.UIAutomation
{
    public interface IUIAutomationProvider : IDisposable
    {
        // Platform identification
        string PlatformName { get; }
        bool IsSupported { get; }
        
        // Screen Capture
        Task<CaptureResult> CaptureScreenAsync(CaptureOptions options);
        Task<CaptureResult> CaptureWindowAsync(string windowTitle, CaptureOptions options);
        Task<CaptureResult> CaptureWindowAsync(IntPtr windowHandle, CaptureOptions options);
        Task<CaptureResult> CaptureRegionAsync(int x, int y, int width, int height, CaptureOptions options);
        
        // Window Management
        Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(bool includeHidden = false, string? titleFilter = null);
        Task<WindowInfo?> GetActiveWindowAsync();
        Task<bool> FocusWindowAsync(string windowTitle);
        Task<bool> FocusWindowAsync(IntPtr windowHandle);
        
        // Mouse Input
        Task<bool> ClickAsync(int x, int y, ClickOptions? options = null);
        Task<bool> DoubleClickAsync(int x, int y, ClickOptions? options = null);
        Task<bool> RightClickAsync(int x, int y, ClickOptions? options = null);
        Task<bool> MoveMouseAsync(int x, int y);
        Task<(int X, int Y)> GetMousePositionAsync();
        
        // Keyboard Input
        Task<bool> TypeTextAsync(string text, TypeOptions? options = null);
        Task<bool> SendKeysAsync(string[] keys, TypeOptions? options = null);
        
        // UI Element Inspection (Phase 2)
        Task<UIElement?> GetElementAtAsync(int x, int y);
        Task<IReadOnlyList<UIElement>> FindElementsAsync(string selector);
        Task<UIElement?> GetFocusedElementAsync();
    }
}
```

### 3.2 Model Classes

```csharp
// WindowInfo.cs
public class WindowInfo
{
    public IntPtr Handle { get; set; }
    public string Title { get; set; } = "";
    public string ProcessName { get; set; } = "";
    public int ProcessId { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool IsVisible { get; set; }
    public bool IsMinimized { get; set; }
    public bool IsMaximized { get; set; }
    public bool IsForeground { get; set; }
}

// CaptureResult.cs
public class CaptureResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? Base64Data { get; set; }
    public string? FilePath { get; set; }
    public string MimeType { get; set; } = "image/png";
    public int Width { get; set; }
    public int Height { get; set; }
    public long FileSizeBytes { get; set; }
}

// CaptureOptions.cs
public class CaptureOptions
{
    public string Format { get; set; } = "png";        // png, jpeg
    public int JpegQuality { get; set; } = 85;         // 1-100
    public string Output { get; set; } = "base64";     // base64, file
    public string? FilePath { get; set; }              // If output=file
    public bool IncludeCursor { get; set; } = false;
}

// ClickOptions.cs
public class ClickOptions
{
    public string Button { get; set; } = "left";       // left, right, middle
    public int Clicks { get; set; } = 1;               // 1=single, 2=double
    public int DelayMs { get; set; } = 50;             // Delay between clicks
    public bool WindowRelative { get; set; } = false;  // Coords relative to window
    public string? WindowTitle { get; set; }           // Target window for relative
}

// TypeOptions.cs
public class TypeOptions
{
    public int DelayBetweenKeysMs { get; set; } = 10;
    public bool SendToActiveWindow { get; set; } = true;
    public string? WindowTitle { get; set; }           // Target specific window
}

// UIElement.cs (Phase 2)
public class UIElement
{
    public string AutomationId { get; set; } = "";
    public string Name { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string ControlType { get; set; } = "";      // Button, TextBox, etc.
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool IsEnabled { get; set; }
    public bool IsVisible { get; set; }
    public string? Value { get; set; }                 // For text fields
    public List<UIElement> Children { get; set; } = new();
}
```

---

## 4. Tool Definitions

### 4.1 Tool Schemas (BuildTools.cs additions)

```csharp
// ui_capture - Screenshot capture
new Tool
{
    Type = "function",
    Function = new FunctionDef
    {
        Name = "ui_capture",
        Description = @"Capture a screenshot of the screen, a window, or a region. 
Use for visual debugging, verifying UI state, or feeding images to vision models.

Modes:
- fullscreen: Capture entire primary screen
- window: Capture specific window by title
- region: Capture rectangular area by coordinates",
        Parameters = JsonDocument.Parse("""
        {
          "type":"object",
          "properties":{
            "mode":{"type":"string","enum":["fullscreen","window","region"],"default":"fullscreen"},
            "window_title":{"type":"string","description":"Window title (for mode=window). Partial match supported."},
            "x":{"type":"integer","description":"Left coordinate (for mode=region)"},
            "y":{"type":"integer","description":"Top coordinate (for mode=region)"},
            "width":{"type":"integer","description":"Width in pixels (for mode=region)"},
            "height":{"type":"integer","description":"Height in pixels (for mode=region)"},
            "format":{"type":"string","enum":["png","jpeg"],"default":"png"},
            "quality":{"type":"integer","minimum":1,"maximum":100,"default":85,"description":"JPEG quality (ignored for PNG)"},
            "output":{"type":"string","enum":["base64","file"],"default":"base64"},
            "file_path":{"type":"string","description":"Output file path (for output=file)"}
          }
        }
        """).RootElement
    }
}

// ui_list_windows - Window enumeration
new Tool
{
    Type = "function",
    Function = new FunctionDef
    {
        Name = "ui_list_windows",
        Description = "List all open windows. Use to discover window titles for ui_capture or ui_click.",
        Parameters = JsonDocument.Parse("""
        {
          "type":"object",
          "properties":{
            "include_hidden":{"type":"boolean","default":false,"description":"Include hidden/background windows"},
            "filter":{"type":"string","description":"Filter by title (case-insensitive partial match)"}
          }
        }
        """).RootElement
    }
}

// ui_click - Mouse click
new Tool
{
    Type = "function",
    Function = new FunctionDef
    {
        Name = "ui_click",
        Description = "Click at screen coordinates. Use after ui_capture to interact with UI elements.",
        Parameters = JsonDocument.Parse("""
        {
          "type":"object",
          "properties":{
            "x":{"type":"integer","description":"X coordinate (screen or window-relative)"},
            "y":{"type":"integer","description":"Y coordinate (screen or window-relative)"},
            "button":{"type":"string","enum":["left","right","middle"],"default":"left"},
            "clicks":{"type":"integer","enum":[1,2],"default":1,"description":"1=single click, 2=double click"},
            "window_title":{"type":"string","description":"If provided, coordinates are relative to this window"}
          },
          "required":["x","y"]
        }
        """).RootElement
    }
}

// ui_type - Keyboard input
new Tool
{
    Type = "function",
    Function = new FunctionDef
    {
        Name = "ui_type",
        Description = @"Type text or send keyboard shortcuts to the active window.

For special keys, use the keys array: ['ctrl', 's'], ['alt', 'f4'], ['enter'], ['tab'], etc.
For regular text, use the text parameter.",
        Parameters = JsonDocument.Parse("""
        {
          "type":"object",
          "properties":{
            "text":{"type":"string","description":"Text to type literally"},
            "keys":{"type":"array","items":{"type":"string"},"description":"Special keys to send, e.g. ['ctrl','s'] or ['enter']"},
            "window_title":{"type":"string","description":"Target window (will focus first)"},
            "delay_ms":{"type":"integer","minimum":0,"maximum":1000,"default":10,"description":"Delay between keystrokes"}
          }
        }
        """).RootElement
    }
}

// ui_mouse_move - Move cursor
new Tool
{
    Type = "function",
    Function = new FunctionDef
    {
        Name = "ui_mouse_move",
        Description = "Move the mouse cursor to specified coordinates without clicking.",
        Parameters = JsonDocument.Parse("""
        {
          "type":"object",
          "properties":{
            "x":{"type":"integer","description":"X coordinate"},
            "y":{"type":"integer","description":"Y coordinate"},
            "window_title":{"type":"string","description":"If provided, coordinates are relative to this window"}
          },
          "required":["x","y"]
        }
        """).RootElement
    }
}

// ui_get_element - Element inspection (Phase 2)
new Tool
{
    Type = "function",
    Function = new FunctionDef
    {
        Name = "ui_get_element",
        Description = "Get UI element information at coordinates or by selector. Use for intelligent element targeting.",
        Parameters = JsonDocument.Parse("""
        {
          "type":"object",
          "properties":{
            "x":{"type":"integer","description":"X coordinate to inspect"},
            "y":{"type":"integer","description":"Y coordinate to inspect"},
            "selector":{"type":"string","description":"UI Automation selector (e.g., 'Button:Submit', 'TextBox:Username')"},
            "window_title":{"type":"string","description":"Limit search to specific window"}
          }
        }
        """).RootElement
    }
}

// ui_focus_window - Window focus
new Tool
{
    Type = "function",
    Function = new FunctionDef
    {
        Name = "ui_focus_window",
        Description = "Bring a window to the foreground and give it focus.",
        Parameters = JsonDocument.Parse("""
        {
          "type":"object",
          "properties":{
            "window_title":{"type":"string","description":"Window title (partial match supported)"}
          },
          "required":["window_title"]
        }
        """).RootElement
    }
}

// ui_wait - Wait for UI state (Phase 2)
new Tool
{
    Type = "function",
    Function = new FunctionDef
    {
        Name = "ui_wait",
        Description = "Wait for a window to appear or UI element to become available.",
        Parameters = JsonDocument.Parse("""
        {
          "type":"object",
          "properties":{
            "window_title":{"type":"string","description":"Wait for window with this title"},
            "element_selector":{"type":"string","description":"Wait for UI element matching selector"},
            "timeout_ms":{"type":"integer","minimum":100,"maximum":60000,"default":10000}
          }
        }
        """).RootElement
    }
}
```

---

## 5. Permission System Integration

### 5.1 Global UI Automation Permission

Add new permission category for UI automation tools:

```csharp
// In PermissionManager.cs

// New risk level for UI automation
public enum ToolRiskLevel
{
    ReadOnly,           // Safe tools that only read data
    Write,              // Tools that can modify files/system
    UIAutomation        // Tools that can interact with UI (new)
}

// Separate permission sets
private static readonly HashSet<string> UIReadOnlyTools = new(StringComparer.OrdinalIgnoreCase)
{
    "ui_capture", "ui_list_windows", "ui_get_element"
};

private static readonly HashSet<string> UIWriteTools = new(StringComparer.OrdinalIgnoreCase)
{
    "ui_click", "ui_type", "ui_mouse_move", "ui_focus_window", "ui_wait"
};

// Global UI automation permission flag
public static bool UIAutomationEnabled { get; set; } = false;

// Check method update
public static async Task<bool> CheckPermissionAsync(string toolName, string argsJson)
{
    // Check if this is a UI automation tool
    if (UIReadOnlyTools.Contains(toolName) || UIWriteTools.Contains(toolName))
    {
        // First check global UI permission
        if (!UIAutomationEnabled)
        {
            if (!await PromptForUIAutomationPermissionAsync())
                return false;
            UIAutomationEnabled = true;
        }
        
        // UI read-only tools allowed after global permission
        if (UIReadOnlyTools.Contains(toolName))
            return true;
            
        // UI write tools need additional per-tool permission
        // (falls through to existing logic)
    }
    
    // ... existing permission logic ...
}
```

### 5.2 Configuration Support

```json
// appsettings.json addition
{
  "UIAutomationConfig": {
    "Enabled": false,
    "RequireApprovalForCapture": false,
    "RequireApprovalForInput": true,
    "AllowedWindowPatterns": ["*"],
    "BlockedWindowPatterns": ["*Password*", "*Banking*"],
    "MaxCapturesPerMinute": 60,
    "LogAllActions": true
  }
}
```

---

## 6. Implementation Phases

### Phase 1: Core Capture & Window Listing (Week 1)
**Priority: HIGH**

| Task | Description | Effort |
|------|-------------|--------|
| 1.1 | Create interface and model classes | 2h |
| 1.2 | Implement Win32 P/Invoke declarations | 3h |
| 1.3 | Implement WindowsCapture.cs (GDI+ screen capture) | 4h |
| 1.4 | Implement WindowsWindowManager.cs (EnumWindows) | 3h |
| 1.5 | Create UIAutomationToolImpl.cs (tool entry point) | 2h |
| 1.6 | Add tool definitions to BuildTools.cs | 1h |
| 1.7 | Add tool dispatch to ToolExecutor.cs | 1h |
| 1.8 | Add UI automation permission to PermissionManager.cs | 2h |
| 1.9 | Integration testing | 2h |

**Deliverables:**
- `ui_capture` tool (fullscreen, window, region modes)
- `ui_list_windows` tool
- `ui_focus_window` tool
- Global UI automation permission prompt

### Phase 2: Input Controls & Element Inspection (Week 2)
**Priority: MEDIUM**

| Task | Description | Effort |
|------|-------------|--------|
| 2.1 | Implement WindowsInput.cs (SendInput API) | 4h |
| 2.2 | Implement mouse click, double-click, right-click | 2h |
| 2.3 | Implement keyboard text input | 2h |
| 2.4 | Implement keyboard shortcuts (Ctrl+S, etc.) | 2h |
| 2.5 | Add UI Automation element inspection | 4h |
| 2.6 | Implement ui_get_element with selector support | 3h |
| 2.7 | Implement ui_wait for synchronization | 2h |
| 2.8 | Add tool definitions and dispatch | 1h |
| 2.9 | Integration testing | 2h |

**Deliverables:**
- `ui_click` tool
- `ui_type` tool
- `ui_mouse_move` tool
- `ui_get_element` tool
- `ui_wait` tool

### Phase 3: Cross-Platform Support (Future)
**Priority: LOW**

| Task | Description | Effort |
|------|-------------|--------|
| 3.1 | Linux provider using X11/libxdo | 8h |
| 3.2 | macOS provider using CoreGraphics | 8h |
| 3.3 | Platform-specific testing | 4h |

---

## 7. Windows Implementation Details

### 7.1 Screen Capture (GDI+)

```csharp
// WindowsCapture.cs - Core capture logic

using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class WindowsCapture
{
    public static CaptureResult CaptureFullScreen(CaptureOptions options)
    {
        // Get virtual screen bounds (all monitors)
        int left = NativeMethods.GetSystemMetrics(SM_XVIRTUALSCREEN);
        int top = NativeMethods.GetSystemMetrics(SM_YVIRTUALSCREEN);
        int width = NativeMethods.GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int height = NativeMethods.GetSystemMetrics(SM_CYVIRTUALSCREEN);
        
        return CaptureRegion(left, top, width, height, options);
    }
    
    public static CaptureResult CaptureWindow(IntPtr hwnd, CaptureOptions options)
    {
        // Get window rect
        if (!NativeMethods.GetWindowRect(hwnd, out RECT rect))
            return new CaptureResult { Success = false, Error = "Failed to get window rect" };
        
        // Optional: Use DwmGetWindowAttribute for accurate bounds on Aero
        // Optional: Use PrintWindow for obscured windows
        
        return CaptureRegion(rect.Left, rect.Top, 
            rect.Right - rect.Left, rect.Bottom - rect.Top, options);
    }
    
    public static CaptureResult CaptureRegion(int x, int y, int width, int height, CaptureOptions options)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        
        // Capture screen
        graphics.CopyFromScreen(x, y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
        
        // Optionally capture cursor
        if (options.IncludeCursor)
            DrawCursor(graphics, x, y);
        
        // Convert to output format
        return ConvertToResult(bitmap, options);
    }
    
    private static CaptureResult ConvertToResult(Bitmap bitmap, CaptureOptions options)
    {
        using var ms = new MemoryStream();
        
        var format = options.Format.ToLower() == "jpeg" ? ImageFormat.Jpeg : ImageFormat.Png;
        var mimeType = options.Format.ToLower() == "jpeg" ? "image/jpeg" : "image/png";
        
        if (format == ImageFormat.Jpeg)
        {
            var encoder = GetEncoder(ImageFormat.Jpeg);
            var encoderParams = new EncoderParameters(1);
            encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, options.JpegQuality);
            bitmap.Save(ms, encoder, encoderParams);
        }
        else
        {
            bitmap.Save(ms, format);
        }
        
        var result = new CaptureResult
        {
            Success = true,
            Width = bitmap.Width,
            Height = bitmap.Height,
            MimeType = mimeType,
            FileSizeBytes = ms.Length
        };
        
        if (options.Output == "file" && !string.IsNullOrEmpty(options.FilePath))
        {
            File.WriteAllBytes(options.FilePath, ms.ToArray());
            result.FilePath = options.FilePath;
        }
        else
        {
            result.Base64Data = Convert.ToBase64String(ms.ToArray());
        }
        
        return result;
    }
}
```

### 7.2 Window Enumeration

```csharp
// WindowsWindowManager.cs

public static class WindowsWindowManager
{
    public static List<WindowInfo> EnumerateWindows(bool includeHidden, string? filter)
    {
        var windows = new List<WindowInfo>();
        
        NativeMethods.EnumWindows((hwnd, lParam) =>
        {
            // Get window info
            var info = GetWindowInfo(hwnd);
            
            // Filter hidden windows
            if (!includeHidden && !info.IsVisible)
                return true; // Continue enumeration
            
            // Filter by title
            if (!string.IsNullOrEmpty(filter) && 
                !info.Title.Contains(filter, StringComparison.OrdinalIgnoreCase))
                return true;
            
            windows.Add(info);
            return true; // Continue enumeration
        }, IntPtr.Zero);
        
        return windows;
    }
    
    public static WindowInfo? FindWindow(string titlePattern)
    {
        WindowInfo? found = null;
        
        NativeMethods.EnumWindows((hwnd, lParam) =>
        {
            var title = GetWindowTitle(hwnd);
            if (title.Contains(titlePattern, StringComparison.OrdinalIgnoreCase))
            {
                found = GetWindowInfo(hwnd);
                return false; // Stop enumeration
            }
            return true;
        }, IntPtr.Zero);
        
        return found;
    }
}
```

### 7.3 Input Simulation

```csharp
// WindowsInput.cs

public static class WindowsInput
{
    public static void Click(int x, int y, MouseButton button = MouseButton.Left)
    {
        // Move cursor
        NativeMethods.SetCursorPos(x, y);
        
        // Build input structure
        var inputs = new INPUT[2];
        
        inputs[0].type = INPUT_MOUSE;
        inputs[0].mi.dwFlags = button == MouseButton.Left ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_RIGHTDOWN;
        
        inputs[1].type = INPUT_MOUSE;
        inputs[1].mi.dwFlags = button == MouseButton.Left ? MOUSEEVENTF_LEFTUP : MOUSEEVENTF_RIGHTUP;
        
        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }
    
    public static void TypeText(string text, int delayMs = 10)
    {
        foreach (char c in text)
        {
            // Use SendInput with KEYEVENTF_UNICODE for proper Unicode support
            var inputs = new INPUT[2];
            
            inputs[0].type = INPUT_KEYBOARD;
            inputs[0].ki.wVk = 0;
            inputs[0].ki.wScan = c;
            inputs[0].ki.dwFlags = KEYEVENTF_UNICODE;
            
            inputs[1].type = INPUT_KEYBOARD;
            inputs[1].ki.wVk = 0;
            inputs[1].ki.wScan = c;
            inputs[1].ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
            
            NativeMethods.SendInput(2, inputs, Marshal.SizeOf<INPUT>());
            
            if (delayMs > 0)
                Thread.Sleep(delayMs);
        }
    }
    
    public static void SendKeyCombo(params VirtualKey[] keys)
    {
        // Press all keys down
        foreach (var key in keys)
            SendKeyDown(key);
        
        // Release all keys up (reverse order)
        foreach (var key in keys.Reverse())
            SendKeyUp(key);
    }
}
```

---

## 8. NuGet Dependencies

### 8.1 Required Packages

| Package | Version | License | Purpose |
|---------|---------|---------|---------|
| System.Drawing.Common | 8.0.0 | MIT | GDI+ bitmap operations |

### 8.2 Optional Packages (if needed)

| Package | Version | License | Purpose |
|---------|---------|---------|---------|
| Interop.UIAutomationClient | - | MS-PL | UI Automation COM interop |

**Note:** Core functionality uses P/Invoke and built-in .NET types. No external dependencies required for basic capture and input.

---

## 9. Testing Strategy

### 9.1 Unit Tests

```csharp
// Tests/UIAutomation/CaptureTests.cs
[Fact]
public async Task CaptureFullScreen_ReturnsValidBase64()
{
    var provider = UIAutomationFactory.Create();
    var result = await provider.CaptureScreenAsync(new CaptureOptions());
    
    Assert.True(result.Success);
    Assert.NotNull(result.Base64Data);
    Assert.True(result.Width > 0);
    Assert.True(result.Height > 0);
}

[Fact]
public async Task ListWindows_ReturnsAtLeastOneWindow()
{
    var provider = UIAutomationFactory.Create();
    var windows = await provider.ListWindowsAsync();
    
    Assert.NotEmpty(windows);
    Assert.All(windows, w => Assert.False(string.IsNullOrEmpty(w.Title) && w.Handle == IntPtr.Zero));
}
```

### 9.2 Integration Tests

- Capture screenshot and verify it can be analyzed by vision model
- List windows and verify known application appears
- Click and type in a test application

---

## 10. Security Considerations

### 10.1 Risks

| Risk | Mitigation |
|------|------------|
| Screen capture of sensitive data | Global permission prompt, window blacklist |
| Unintended clicks/keystrokes | Per-tool permission, rate limiting |
| Capture of other users' windows | Process isolation (Windows handles this) |
| Credential theft via OCR | Block windows with "password" in title |

### 10.2 Logging

All UI automation actions are logged:
```
[UI] 2025-01-15 10:30:45 ui_capture mode=window title="My App" size=1920x1080
[UI] 2025-01-15 10:30:47 ui_click x=500 y=300 button=left window="My App"
[UI] 2025-01-15 10:30:48 ui_type text="[REDACTED]" window="My App"
```

---

## 11. Usage Examples

### 11.1 Debug Cycle Workflow

```
User: "Run the calculator app and verify 2+2=4"

Agent:
1. dotnet_run project="Calculator"
2. ui_wait window_title="Calculator" timeout_ms=5000
3. ui_capture mode=window window_title="Calculator"
   → Analyze screenshot to find button positions
4. ui_click x=100 y=200  (button "2")
5. ui_click x=150 y=200  (button "+")
6. ui_click x=100 y=200  (button "2")
7. ui_click x=150 y=250  (button "=")
8. ui_capture mode=window window_title="Calculator"
   → Analyze screenshot to verify "4" is displayed
9. Report: "Verified 2+2=4 displays correctly"
```

### 11.2 Form Filling

```
Agent:
1. ui_list_windows filter="Login"
2. ui_focus_window window_title="Login Form"
3. ui_capture mode=window window_title="Login Form"
4. ui_get_element selector="TextBox:Username"
5. ui_click x={element.X + 10} y={element.Y + 10}
6. ui_type text="testuser"
7. ui_type keys=["tab"]
8. ui_type text="password123"
9. ui_click selector="Button:Submit"
10. ui_wait window_title="Dashboard" timeout_ms=10000
11. ui_capture mode=window window_title="Dashboard"
```

---

## 12. Future Enhancements

- **OCR Integration**: Extract text from screenshots for validation
- **Visual diff**: Compare screenshots to detect changes
- **Record/Playback**: Record UI interactions for replay
- **Multi-monitor support**: Capture specific monitor
- **Accessibility tree**: Deep integration with Windows UI Automation

---

## 13. Appendix: Win32 API Reference

### Key Functions Used

| Function | Purpose |
|----------|---------|
| `EnumWindows` | List all top-level windows |
| `GetWindowRect` | Get window position and size |
| `GetWindowText` | Get window title |
| `GetForegroundWindow` | Get active window |
| `SetForegroundWindow` | Focus a window |
| `SetCursorPos` | Move mouse cursor |
| `SendInput` | Simulate mouse/keyboard input |
| `GetCursorPos` | Get current cursor position |
| `GetSystemMetrics` | Get screen dimensions |

### UI Automation COM Interfaces (Phase 2)

| Interface | Purpose |
|-----------|---------|
| `IUIAutomation` | Entry point for UI Automation |
| `IUIAutomationElement` | Represents UI element |
| `IUIAutomationTreeWalker` | Navigate element tree |
| `IUIAutomationCondition` | Filter elements |

---

**Document Version History**

| Version | Date | Author | Changes |
|---------|------|--------|---------|
| 1.0 | 2025-01-15 | Agent | Initial plan |
| 1.1 | 2025-01-15 | Agent | Phase 1 complete (capture, windows, input) |
| 2.0 | 2025-01-16 | Agent | Phase 2 complete (element inspection, wait tools) |

---

## Implementation Status Summary

### Completed (8 tools):
- ✅ `ui_capture` - Screenshot capture (fullscreen, window, region)
- ✅ `ui_list_windows` - Enumerate windows
- ✅ `ui_focus_window` - Focus/activate window
- ✅ `ui_click` - Mouse click at coordinates
- ✅ `ui_type` - Keyboard input (text and shortcuts)
- ✅ `ui_mouse_move` - Move cursor
- ✅ `ui_get_element` - Get UI element at point or by selector
- ✅ `ui_wait` - Wait for window or element

### Technologies Used:
- **Screen Capture**: System.Drawing.Common (GDI+)
- **Window Management**: Win32 P/Invoke (EnumWindows, GetWindowRect, etc.)
- **Input Simulation**: SendInput API
- **UI Automation**: FlaUI.UIA3 (MIT licensed, .NET wrapper for Windows UI Automation)

### Vision Integration
The `ui_capture` tool now supports direct vision model integration:
- **`analyze`** parameter: When `true`, automatically sends captured image to vision model
- **`analyze_prompt`** parameter: Custom prompt for vision analysis (default: "Describe what you see in this screenshot")
- Includes full conversation context when calling vision model for better understanding
- Returns analysis text instead of base64 data (token-efficient)

Example usage:
```json
{
  "tool": "ui_capture",
  "parameters": {
    "mode": "window",
    "window_title": "My App",
    "analyze": true,
    "analyze_prompt": "Identify any error messages or warnings visible in this application window"
  }
}
```
