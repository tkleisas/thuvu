using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using thuvu.Models;
using thuvu.Tools.UIAutomation.Models;

namespace thuvu.Tools.UIAutomation
{
    /// <summary>
    /// Tool implementation for UI automation operations.
    /// Entry point for all ui_* tools.
    /// </summary>
    public static class UIAutomationToolImpl
    {
        private static IUIAutomationProvider? _provider;
        
        /// <summary>
        /// Get or create the UI automation provider
        /// </summary>
        private static IUIAutomationProvider GetProvider()
        {
            _provider ??= UIAutomationFactory.Create();
            return _provider;
        }
        
        #region ui_capture
        
        /// <summary>
        /// Capture screenshot (ui_capture tool)
        /// </summary>
        public static async Task<string> CaptureAsync(string argsJson, CancellationToken ct)
        {
            // Get conversation context from AgentContext if available
            var conversationContext = AgentContext.GetCurrentMessages();
            return await CaptureWithContextAsync(argsJson, conversationContext, ct);
        }
        
        /// <summary>
        /// Capture screenshot with optional conversation context for vision analysis
        /// </summary>
        public static async Task<string> CaptureWithContextAsync(string argsJson, List<ChatMessage>? conversationContext, CancellationToken ct)
        {
            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                var root = doc.RootElement;
                
                var mode = GetStringProperty(root, "mode", "fullscreen");
                var analyze = GetBoolProperty(root, "analyze", false);
                var analyzePrompt = GetStringProperty(root, "analyze_prompt", "Describe what you see in this screenshot. Focus on UI elements, text content, and any notable visual details.");
                
                var options = new CaptureOptions
                {
                    Format = GetStringProperty(root, "format", "png"),
                    JpegQuality = GetIntProperty(root, "quality", 85),
                    Output = GetStringProperty(root, "output", "base64"),
                    FilePath = GetStringProperty(root, "file_path", null),
                    IncludeCursor = GetBoolProperty(root, "include_cursor", false)
                };
                
                // If analyze is requested, we need base64 data regardless of output setting
                if (analyze && options.Output == "file")
                {
                    // Keep file output but also get base64 for analysis
                    options.Output = "base64";
                }
                
                var provider = GetProvider();
                CaptureResult result;
                
                switch (mode.ToLowerInvariant())
                {
                    case "window":
                        var windowTitle = GetStringProperty(root, "window_title", null);
                        if (string.IsNullOrEmpty(windowTitle))
                        {
                            return JsonSerializer.Serialize(new { 
                                success = false, 
                                error = "window_title is required for mode=window" 
                            });
                        }
                        result = await provider.CaptureWindowAsync(windowTitle, options);
                        break;
                        
                    case "region":
                        var x = GetIntProperty(root, "x", 0);
                        var y = GetIntProperty(root, "y", 0);
                        var width = GetIntProperty(root, "width", 0);
                        var height = GetIntProperty(root, "height", 0);
                        
                        if (width <= 0 || height <= 0)
                        {
                            return JsonSerializer.Serialize(new { 
                                success = false, 
                                error = "width and height are required for mode=region" 
                            });
                        }
                        result = await provider.CaptureRegionAsync(x, y, width, height, options);
                        break;
                        
                    default: // fullscreen
                        result = await provider.CaptureScreenAsync(options);
                        break;
                }
                
                if (result.Success)
                {
                    var response = new Dictionary<string, object?>
                    {
                        ["success"] = true,
                        ["width"] = result.Width,
                        ["height"] = result.Height,
                        ["mime_type"] = result.MimeType,
                        ["size_bytes"] = result.FileSizeBytes
                    };
                    
                    if (!string.IsNullOrEmpty(result.FilePath))
                        response["file_path"] = result.FilePath;
                    if (!string.IsNullOrEmpty(result.WindowTitle))
                        response["window_title"] = result.WindowTitle;
                    
                    // If analyze requested, call vision model
                    if (analyze && !string.IsNullOrEmpty(result.Base64Data))
                    {
                        var visionResult = await VisionToolImpl.AnalyzeImageWithContextAsync(
                            conversationContext,
                            result.Base64Data,
                            result.MimeType,
                            analyzePrompt ?? "Describe what you see in this screenshot.",
                            ct);
                        
                        if (visionResult.Success)
                        {
                            response["analysis"] = visionResult.Description;
                            response["vision_model"] = visionResult.Model;
                            // Don't include base64 when analyzing - it's huge and the model already saw it
                            // response["base64_data"] = result.Base64Data; // Omit to reduce token usage
                        }
                        else
                        {
                            response["analysis_error"] = visionResult.Error;
                            // Still include base64 if analysis failed so caller can retry
                            response["base64_data"] = result.Base64Data;
                        }
                    }
                    else if (!string.IsNullOrEmpty(result.Base64Data))
                    {
                        // No analysis requested, include base64 data
                        response["base64_data"] = result.Base64Data;
                    }
                    
                    return JsonSerializer.Serialize(response);
                }
                else
                {
                    return JsonSerializer.Serialize(new { success = false, error = result.Error });
                }
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { success = false, error = ex.Message });
            }
        }
        
        #endregion
        
        #region ui_list_windows
        
        /// <summary>
        /// List open windows (ui_list_windows tool)
        /// </summary>
        public static async Task<string> ListWindowsAsync(string argsJson, CancellationToken ct)
        {
            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                var root = doc.RootElement;
                
                var includeHidden = GetBoolProperty(root, "include_hidden", false);
                var filter = GetStringProperty(root, "filter", null);
                
                var provider = GetProvider();
                var windows = await provider.ListWindowsAsync(includeHidden, filter);
                
                var windowList = windows.Select(w => new
                {
                    handle = w.Handle.ToInt64(),
                    title = w.Title,
                    process_name = w.ProcessName,
                    process_id = w.ProcessId,
                    x = w.X,
                    y = w.Y,
                    width = w.Width,
                    height = w.Height,
                    is_visible = w.IsVisible,
                    is_minimized = w.IsMinimized,
                    is_maximized = w.IsMaximized,
                    is_foreground = w.IsForeground
                }).ToList();
                
                return JsonSerializer.Serialize(new
                {
                    success = true,
                    count = windowList.Count,
                    windows = windowList
                });
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { success = false, error = ex.Message });
            }
        }
        
        #endregion
        
        #region ui_focus_window
        
        /// <summary>
        /// Focus a window (ui_focus_window tool)
        /// </summary>
        public static async Task<string> FocusWindowAsync(string argsJson, CancellationToken ct)
        {
            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                var root = doc.RootElement;
                
                var windowTitle = GetStringProperty(root, "window_title", null);
                if (string.IsNullOrEmpty(windowTitle))
                {
                    return JsonSerializer.Serialize(new { 
                        success = false, 
                        error = "window_title is required" 
                    });
                }
                
                var provider = GetProvider();
                var result = await provider.FocusWindowAsync(windowTitle);
                
                return JsonSerializer.Serialize(new
                {
                    success = result,
                    window_title = windowTitle,
                    error = result ? null : $"Failed to focus window: '{windowTitle}'"
                });
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { success = false, error = ex.Message });
            }
        }
        
        #endregion
        
        #region ui_click
        
        /// <summary>
        /// Mouse click (ui_click tool)
        /// </summary>
        public static async Task<string> ClickAsync(string argsJson, CancellationToken ct)
        {
            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                var root = doc.RootElement;
                
                if (!root.TryGetProperty("x", out _) || !root.TryGetProperty("y", out _))
                {
                    return JsonSerializer.Serialize(new { 
                        success = false, 
                        error = "x and y coordinates are required" 
                    });
                }
                
                var x = GetIntProperty(root, "x", 0);
                var y = GetIntProperty(root, "y", 0);
                var windowTitle = GetStringProperty(root, "window_title", null);
                
                var options = new ClickOptions
                {
                    Button = GetStringProperty(root, "button", "left"),
                    Clicks = GetIntProperty(root, "clicks", 1),
                    WindowRelative = !string.IsNullOrEmpty(windowTitle),
                    WindowTitle = windowTitle
                };
                
                var provider = GetProvider();
                var result = await provider.ClickAsync(x, y, options);
                
                return JsonSerializer.Serialize(new
                {
                    success = result,
                    x,
                    y,
                    button = options.Button,
                    clicks = options.Clicks,
                    window_relative = options.WindowRelative,
                    error = result ? null : "Click failed"
                });
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { success = false, error = ex.Message });
            }
        }
        
        #endregion
        
        #region ui_type
        
        /// <summary>
        /// Keyboard input (ui_type tool)
        /// </summary>
        public static async Task<string> TypeAsync(string argsJson, CancellationToken ct)
        {
            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                var root = doc.RootElement;
                
                var text = GetStringProperty(root, "text", null);
                var windowTitle = GetStringProperty(root, "window_title", null);
                var delayMs = GetIntProperty(root, "delay_ms", 10);
                
                // Get keys array if present
                string[]? keys = null;
                if (root.TryGetProperty("keys", out var keysElement) && 
                    keysElement.ValueKind == JsonValueKind.Array)
                {
                    keys = keysElement.EnumerateArray()
                        .Select(k => k.GetString() ?? "")
                        .Where(k => !string.IsNullOrEmpty(k))
                        .ToArray();
                }
                
                var options = new TypeOptions
                {
                    DelayBetweenKeysMs = delayMs,
                    WindowTitle = windowTitle
                };
                
                var provider = GetProvider();
                bool result;
                
                if (keys != null && keys.Length > 0)
                {
                    // Send keyboard shortcut
                    result = await provider.SendKeysAsync(keys, options);
                    return JsonSerializer.Serialize(new
                    {
                        success = result,
                        keys,
                        window_title = windowTitle,
                        error = result ? null : "Failed to send keys"
                    });
                }
                else if (!string.IsNullOrEmpty(text))
                {
                    // Type text
                    result = await provider.TypeTextAsync(text, options);
                    return JsonSerializer.Serialize(new
                    {
                        success = result,
                        text_length = text.Length,
                        window_title = windowTitle,
                        error = result ? null : "Failed to type text"
                    });
                }
                else
                {
                    return JsonSerializer.Serialize(new { 
                        success = false, 
                        error = "Either 'text' or 'keys' must be provided" 
                    });
                }
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { success = false, error = ex.Message });
            }
        }
        
        #endregion
        
        #region ui_mouse_move
        
        /// <summary>
        /// Move mouse cursor (ui_mouse_move tool)
        /// </summary>
        public static async Task<string> MoveMouseAsync(string argsJson, CancellationToken ct)
        {
            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                var root = doc.RootElement;
                
                if (!root.TryGetProperty("x", out _) || !root.TryGetProperty("y", out _))
                {
                    return JsonSerializer.Serialize(new { 
                        success = false, 
                        error = "x and y coordinates are required" 
                    });
                }
                
                var x = GetIntProperty(root, "x", 0);
                var y = GetIntProperty(root, "y", 0);
                var windowTitle = GetStringProperty(root, "window_title", null);
                
                var provider = GetProvider();
                
                // Convert to absolute coordinates if window-relative
                int absX = x, absY = y;
                if (!string.IsNullOrEmpty(windowTitle))
                {
                    var windows = await provider.ListWindowsAsync(false, windowTitle);
                    var window = windows.FirstOrDefault();
                    if (window == null)
                    {
                        return JsonSerializer.Serialize(new { 
                            success = false, 
                            error = $"Window not found: '{windowTitle}'" 
                        });
                    }
                    absX = window.X + x;
                    absY = window.Y + y;
                }
                
                var result = await provider.MoveMouseAsync(absX, absY);
                
                return JsonSerializer.Serialize(new
                {
                    success = result,
                    x = absX,
                    y = absY,
                    error = result ? null : "Failed to move mouse"
                });
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { success = false, error = ex.Message });
            }
        }
        
        #endregion
        
        #region Helper methods
        
        private static string? GetStringProperty(JsonElement root, string name, string? defaultValue)
        {
            if (root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
                return prop.GetString();
            return defaultValue;
        }
        
        private static int GetIntProperty(JsonElement root, string name, int defaultValue)
        {
            if (root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number)
                return prop.GetInt32();
            return defaultValue;
        }
        
        private static bool GetBoolProperty(JsonElement root, string name, bool defaultValue)
        {
            if (root.TryGetProperty(name, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.True) return true;
                if (prop.ValueKind == JsonValueKind.False) return false;
            }
            return defaultValue;
        }
        
        #endregion
        
        #region ui_get_element
        
        /// <summary>
        /// Get UI element at coordinates or by selector (ui_get_element tool)
        /// </summary>
        public static async Task<string> GetElementAsync(string argsJson, CancellationToken ct)
        {
            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                var root = doc.RootElement;
                
                var x = GetIntProperty(root, "x", -1);
                var y = GetIntProperty(root, "y", -1);
                var selector = GetStringProperty(root, "selector", null);
                var windowTitle = GetStringProperty(root, "window_title", null);
                
                var provider = GetProvider() as Windows.WindowsUIProvider;
                if (provider == null)
                {
                    return JsonSerializer.Serialize(new { 
                        success = false, 
                        error = "UI element inspection not supported on this platform" 
                    });
                }
                
                // If coordinates provided, get element at point
                if (x >= 0 && y >= 0)
                {
                    var element = await provider.GetElementAtAsync(x, y);
                    if (element != null)
                    {
                        return JsonSerializer.Serialize(new
                        {
                            success = true,
                            mode = "point",
                            x,
                            y,
                            element = ConvertElementToDict(element)
                        });
                    }
                    else
                    {
                        return JsonSerializer.Serialize(new { 
                            success = false, 
                            error = $"No UI element found at ({x}, {y})" 
                        });
                    }
                }
                
                // If selector provided, find elements
                if (!string.IsNullOrEmpty(selector))
                {
                    var elements = await provider.FindElementsAsync(selector, windowTitle);
                    if (elements.Count > 0)
                    {
                        return JsonSerializer.Serialize(new
                        {
                            success = true,
                            mode = "selector",
                            selector,
                            window_title = windowTitle,
                            count = elements.Count,
                            elements = elements.Select(ConvertElementToDict).ToList()
                        });
                    }
                    else
                    {
                        return JsonSerializer.Serialize(new { 
                            success = false, 
                            error = $"No elements found matching selector: {selector}" 
                        });
                    }
                }
                
                return JsonSerializer.Serialize(new { 
                    success = false, 
                    error = "Either x/y coordinates or selector must be provided" 
                });
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { success = false, error = ex.Message });
            }
        }
        
        #endregion
        
        #region ui_wait
        
        /// <summary>
        /// Wait for window or element to appear (ui_wait tool)
        /// </summary>
        public static async Task<string> WaitAsync(string argsJson, CancellationToken ct)
        {
            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                var root = doc.RootElement;
                
                var windowTitle = GetStringProperty(root, "window_title", null);
                var elementSelector = GetStringProperty(root, "element_selector", null);
                var timeoutMs = GetIntProperty(root, "timeout_ms", 10000);
                
                var provider = GetProvider() as Windows.WindowsUIProvider;
                if (provider == null)
                {
                    return JsonSerializer.Serialize(new { 
                        success = false, 
                        error = "UI wait not supported on this platform" 
                    });
                }
                
                var startTime = DateTime.Now;
                
                // Wait for window
                if (!string.IsNullOrEmpty(windowTitle))
                {
                    var found = await provider.WaitForWindowAsync(windowTitle, timeoutMs);
                    var elapsed = (DateTime.Now - startTime).TotalMilliseconds;
                    
                    if (found)
                    {
                        return JsonSerializer.Serialize(new
                        {
                            success = true,
                            waited_for = "window",
                            window_title = windowTitle,
                            elapsed_ms = (int)elapsed
                        });
                    }
                    else
                    {
                        return JsonSerializer.Serialize(new
                        {
                            success = false,
                            error = $"Window '{windowTitle}' did not appear within {timeoutMs}ms",
                            elapsed_ms = (int)elapsed
                        });
                    }
                }
                
                // Wait for element
                if (!string.IsNullOrEmpty(elementSelector))
                {
                    var element = await provider.WaitForElementAsync(elementSelector, null, timeoutMs);
                    var elapsed = (DateTime.Now - startTime).TotalMilliseconds;
                    
                    if (element != null)
                    {
                        return JsonSerializer.Serialize(new
                        {
                            success = true,
                            waited_for = "element",
                            selector = elementSelector,
                            elapsed_ms = (int)elapsed,
                            element = ConvertElementToDict(element)
                        });
                    }
                    else
                    {
                        return JsonSerializer.Serialize(new
                        {
                            success = false,
                            error = $"Element '{elementSelector}' did not appear within {timeoutMs}ms",
                            elapsed_ms = (int)elapsed
                        });
                    }
                }
                
                return JsonSerializer.Serialize(new { 
                    success = false, 
                    error = "Either window_title or element_selector must be provided" 
                });
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { success = false, error = ex.Message });
            }
        }
        
        #endregion
        
        #region Helper: Element to Dictionary
        
        private static Dictionary<string, object?> ConvertElementToDict(Models.UIElement element)
        {
            return new Dictionary<string, object?>
            {
                ["automation_id"] = element.AutomationId,
                ["name"] = element.Name,
                ["class_name"] = element.ClassName,
                ["control_type"] = element.ControlType,
                ["x"] = element.X,
                ["y"] = element.Y,
                ["width"] = element.Width,
                ["height"] = element.Height,
                ["center_x"] = element.X + element.Width / 2,
                ["center_y"] = element.Y + element.Height / 2,
                ["is_enabled"] = element.IsEnabled,
                ["is_visible"] = element.IsVisible,
                ["value"] = element.Value
            };
        }
        
        #endregion
    }
}
