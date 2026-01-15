using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using thuvu.Tools.UIAutomation;

namespace thuvu.Tests
{
    /// <summary>
    /// Simple test runner for UI automation tools
    /// </summary>
    public static class UIAutomationTest
    {
        public static async Task RunTests()
        {
            Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║           UI AUTOMATION TOOLS - TEST SUITE                   ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
            Console.WriteLine();
            
            // Test 1: List Windows
            await TestListWindows();
            
            // Test 2: Capture fullscreen
            await TestCaptureFullscreen();
            
            // Test 3: Capture specific window
            await TestCaptureWindow();
            
            // Test 4: Save screenshot to file
            await TestCaptureToFile();
            
            // Test 5: Mouse position
            await TestMousePosition();
            
            // Test 6: Get element at coordinates
            await TestGetElementAtCoordinates();
            
            // Test 7: Find elements by selector
            await TestFindElementsBySelector();
            
            // Test 8: Wait for window
            await TestWaitForWindow();
            
            Console.WriteLine();
            Console.WriteLine("All tests completed!");
        }
        
        private static async Task TestListWindows()
        {
            Console.WriteLine("─── Test 1: ui_list_windows ───");
            try
            {
                var result = await UIAutomationToolImpl.ListWindowsAsync(
                    JsonSerializer.Serialize(new { include_hidden = false }), 
                    CancellationToken.None);
                
                using var doc = JsonDocument.Parse(result);
                var root = doc.RootElement;
                
                if (root.TryGetProperty("success", out var success) && success.GetBoolean())
                {
                    var count = root.GetProperty("count").GetInt32();
                    Console.WriteLine($"  ✓ SUCCESS: Found {count} windows");
                    
                    // Show first 5 windows
                    if (root.TryGetProperty("windows", out var windows))
                    {
                        int shown = 0;
                        foreach (var win in windows.EnumerateArray())
                        {
                            if (shown >= 5) break;
                            var title = win.GetProperty("title").GetString();
                            if (!string.IsNullOrWhiteSpace(title))
                            {
                                var processName = win.GetProperty("process_name").GetString();
                                Console.WriteLine($"    - \"{title}\" ({processName})");
                                shown++;
                            }
                        }
                    }
                }
                else
                {
                    var error = root.GetProperty("error").GetString();
                    Console.WriteLine($"  ✗ FAILED: {error}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ✗ ERROR: {ex.Message}");
            }
            Console.WriteLine();
        }
        
        private static async Task TestCaptureFullscreen()
        {
            Console.WriteLine("─── Test 2: ui_capture (fullscreen) ───");
            try
            {
                var result = await UIAutomationToolImpl.CaptureAsync(
                    JsonSerializer.Serialize(new { mode = "fullscreen", format = "png" }), 
                    CancellationToken.None);
                
                using var doc = JsonDocument.Parse(result);
                var root = doc.RootElement;
                
                if (root.TryGetProperty("success", out var success) && success.GetBoolean())
                {
                    var width = root.GetProperty("width").GetInt32();
                    var height = root.GetProperty("height").GetInt32();
                    var sizeBytes = root.GetProperty("size_bytes").GetInt64();
                    var hasBase64 = root.TryGetProperty("base64_data", out var b64) && 
                                   !string.IsNullOrEmpty(b64.GetString());
                    
                    Console.WriteLine($"  ✓ SUCCESS: Captured {width}x{height} ({sizeBytes:N0} bytes)");
                    Console.WriteLine($"    Base64 data present: {hasBase64}");
                    if (hasBase64)
                    {
                        Console.WriteLine($"    Base64 length: {b64.GetString()!.Length:N0} chars");
                    }
                }
                else
                {
                    var error = root.GetProperty("error").GetString();
                    Console.WriteLine($"  ✗ FAILED: {error}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ✗ ERROR: {ex.Message}");
            }
            Console.WriteLine();
        }
        
        private static async Task TestCaptureWindow()
        {
            Console.WriteLine("─── Test 3: ui_capture (window - first available) ───");
            try
            {
                // First get a window title
                var listResult = await UIAutomationToolImpl.ListWindowsAsync(
                    JsonSerializer.Serialize(new { include_hidden = false }), 
                    CancellationToken.None);
                
                using var listDoc = JsonDocument.Parse(listResult);
                string? windowTitle = null;
                
                if (listDoc.RootElement.TryGetProperty("windows", out var windows))
                {
                    foreach (var win in windows.EnumerateArray())
                    {
                        var title = win.GetProperty("title").GetString();
                        var isVisible = win.GetProperty("is_visible").GetBoolean();
                        var width = win.GetProperty("width").GetInt32();
                        
                        if (!string.IsNullOrWhiteSpace(title) && isVisible && width > 100)
                        {
                            windowTitle = title;
                            break;
                        }
                    }
                }
                
                if (windowTitle == null)
                {
                    Console.WriteLine("  ⚠ SKIPPED: No suitable window found");
                    return;
                }
                
                Console.WriteLine($"  Capturing window: \"{windowTitle}\"");
                
                var result = await UIAutomationToolImpl.CaptureAsync(
                    JsonSerializer.Serialize(new { mode = "window", window_title = windowTitle, format = "jpeg", quality = 80 }), 
                    CancellationToken.None);
                
                using var doc = JsonDocument.Parse(result);
                var root = doc.RootElement;
                
                if (root.TryGetProperty("success", out var success) && success.GetBoolean())
                {
                    var width = root.GetProperty("width").GetInt32();
                    var height = root.GetProperty("height").GetInt32();
                    var sizeBytes = root.GetProperty("size_bytes").GetInt64();
                    
                    Console.WriteLine($"  ✓ SUCCESS: Captured {width}x{height} ({sizeBytes:N0} bytes)");
                }
                else
                {
                    var error = root.GetProperty("error").GetString();
                    Console.WriteLine($"  ✗ FAILED: {error}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ✗ ERROR: {ex.Message}");
            }
            Console.WriteLine();
        }
        
        private static async Task TestCaptureToFile()
        {
            Console.WriteLine("─── Test 4: ui_capture (save to file) ───");
            try
            {
                var filePath = Path.Combine(Path.GetTempPath(), "thuvu_test_screenshot.png");
                
                var result = await UIAutomationToolImpl.CaptureAsync(
                    JsonSerializer.Serialize(new { 
                        mode = "fullscreen", 
                        format = "png", 
                        output = "file", 
                        file_path = filePath 
                    }), 
                    CancellationToken.None);
                
                using var doc = JsonDocument.Parse(result);
                var root = doc.RootElement;
                
                if (root.TryGetProperty("success", out var success) && success.GetBoolean())
                {
                    var width = root.GetProperty("width").GetInt32();
                    var height = root.GetProperty("height").GetInt32();
                    var savedPath = root.GetProperty("file_path").GetString();
                    
                    if (File.Exists(savedPath))
                    {
                        var fileSize = new FileInfo(savedPath!).Length;
                        Console.WriteLine($"  ✓ SUCCESS: Saved {width}x{height} to file ({fileSize:N0} bytes)");
                        Console.WriteLine($"    Path: {savedPath}");
                        
                        // Clean up test file
                        File.Delete(savedPath!);
                        Console.WriteLine($"    (Test file cleaned up)");
                    }
                    else
                    {
                        Console.WriteLine($"  ✗ FAILED: File not found at {savedPath}");
                    }
                }
                else
                {
                    var error = root.GetProperty("error").GetString();
                    Console.WriteLine($"  ✗ FAILED: {error}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ✗ ERROR: {ex.Message}");
            }
            Console.WriteLine();
        }
        
        private static async Task TestMousePosition()
        {
            Console.WriteLine("─── Test 5: Mouse position ───");
            try
            {
                var provider = UIAutomationFactory.Create();
                var pos = await provider.GetMousePositionAsync();
                
                Console.WriteLine($"  ✓ SUCCESS: Mouse at ({pos.X}, {pos.Y})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ✗ ERROR: {ex.Message}");
            }
            Console.WriteLine();
        }
        
        private static async Task TestGetElementAtCoordinates()
        {
            Console.WriteLine("─── Test 6: ui_get_element (at coordinates) ───");
            try
            {
                // Get mouse position for testing
                var provider = UIAutomationFactory.Create();
                var mousePos = await provider.GetMousePositionAsync();
                
                Console.WriteLine($"  Testing at coordinates: ({mousePos.X}, {mousePos.Y})");
                
                var result = await UIAutomationToolImpl.GetElementAsync(
                    JsonSerializer.Serialize(new { x = mousePos.X, y = mousePos.Y }), 
                    CancellationToken.None);
                
                using var doc = JsonDocument.Parse(result);
                var root = doc.RootElement;
                
                if (root.TryGetProperty("success", out var success) && success.GetBoolean())
                {
                    var element = root.GetProperty("element");
                    var name = element.TryGetProperty("name", out var n) ? n.GetString() : "(no name)";
                    var controlType = element.GetProperty("control_type").GetString();
                    var className = element.TryGetProperty("class_name", out var cn) ? cn.GetString() : "(none)";
                    
                    Console.WriteLine($"  ✓ SUCCESS: Found element");
                    Console.WriteLine($"    Name: {name}");
                    Console.WriteLine($"    Type: {controlType}");
                    Console.WriteLine($"    Class: {className}");
                }
                else
                {
                    var error = root.TryGetProperty("error", out var e) ? e.GetString() : "Unknown error";
                    Console.WriteLine($"  ✗ FAILED: {error}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ✗ ERROR: {ex.Message}");
            }
            Console.WriteLine();
        }
        
        private static async Task TestFindElementsBySelector()
        {
            Console.WriteLine("─── Test 7: ui_get_element (by selector) ───");
            try
            {
                // Find all buttons on screen (may find 0 if no visible buttons)
                var result = await UIAutomationToolImpl.GetElementAsync(
                    JsonSerializer.Serialize(new { selector = "Button:*" }), 
                    CancellationToken.None);
                
                using var doc = JsonDocument.Parse(result);
                var root = doc.RootElement;
                
                if (root.TryGetProperty("success", out var success) && success.GetBoolean())
                {
                    var count = root.GetProperty("count").GetInt32();
                    Console.WriteLine($"  ✓ SUCCESS: Found {count} Button elements");
                    
                    // Show first 3
                    if (root.TryGetProperty("elements", out var elements))
                    {
                        int shown = 0;
                        foreach (var el in elements.EnumerateArray())
                        {
                            if (shown >= 3) break;
                            var name = el.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : "(unnamed)";
                            var x = el.GetProperty("center_x").GetInt32();
                            var y = el.GetProperty("center_y").GetInt32();
                            Console.WriteLine($"    - \"{name}\" at ({x}, {y})");
                            shown++;
                        }
                        if (count > 3) Console.WriteLine($"    ... and {count - 3} more");
                    }
                }
                else
                {
                    var error = root.TryGetProperty("error", out var e) ? e.GetString() : "Unknown error";
                    Console.WriteLine($"  ⚠ No buttons found (expected if no UI with buttons visible)");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ✗ ERROR: {ex.Message}");
            }
            Console.WriteLine();
        }
        
        private static async Task TestWaitForWindow()
        {
            Console.WriteLine("─── Test 8: ui_wait (for existing window) ───");
            try
            {
                // First get a known window title
                var listResult = await UIAutomationToolImpl.ListWindowsAsync(
                    JsonSerializer.Serialize(new { include_hidden = false }), 
                    CancellationToken.None);
                
                using var listDoc = JsonDocument.Parse(listResult);
                var listRoot = listDoc.RootElement;
                
                string? windowTitle = null;
                if (listRoot.TryGetProperty("windows", out var windows))
                {
                    foreach (var win in windows.EnumerateArray())
                    {
                        var title = win.GetProperty("title").GetString();
                        if (!string.IsNullOrWhiteSpace(title))
                        {
                            windowTitle = title;
                            break;
                        }
                    }
                }
                
                if (windowTitle == null)
                {
                    Console.WriteLine("  ⚠ SKIPPED: No windows available for testing");
                    return;
                }
                
                // Wait for the window (should find it immediately since it exists)
                Console.WriteLine($"  Waiting for window: \"{windowTitle}\"");
                
                var result = await UIAutomationToolImpl.WaitAsync(
                    JsonSerializer.Serialize(new { window_title = windowTitle, timeout_ms = 5000 }), 
                    CancellationToken.None);
                
                using var doc = JsonDocument.Parse(result);
                var root = doc.RootElement;
                
                if (root.TryGetProperty("success", out var success) && success.GetBoolean())
                {
                    var elapsed = root.GetProperty("elapsed_ms").GetInt32();
                    Console.WriteLine($"  ✓ SUCCESS: Window found in {elapsed}ms");
                }
                else
                {
                    var error = root.TryGetProperty("error", out var e) ? e.GetString() : "Unknown error";
                    Console.WriteLine($"  ✗ FAILED: {error}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ✗ ERROR: {ex.Message}");
            }
            Console.WriteLine();
        }
    }
}
