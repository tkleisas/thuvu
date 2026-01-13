using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace thuvu.Tools
{
    /// <summary>
    /// Browser automation tools using Playwright for web browsing and testing
    /// </summary>
    public class BrowserToolImpl : IAsyncDisposable
    {
        private static IPlaywright? _playwright;
        private static IBrowser? _browser;
        private static IPage? _currentPage;
        private static readonly SemaphoreSlim _lock = new(1, 1);
        private static bool _isInitialized;
        private static string _lastError = "";

        /// <summary>
        /// Initialize Playwright and browser
        /// </summary>
        public static async Task<bool> InitializeAsync(bool headless = true)
        {
            await _lock.WaitAsync();
            try
            {
                if (_isInitialized && _browser?.IsConnected == true)
                    return true;

                _playwright = await Playwright.CreateAsync();
                _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = headless,
                    Args = new[] { "--disable-blink-features=AutomationControlled" }
                });
                _currentPage = await _browser.NewPageAsync();
                _isInitialized = true;
                return true;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                return false;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Check if browsers are installed
        /// </summary>
        public static async Task<bool> AreBrowsersInstalledAsync()
        {
            try
            {
                var exitCode = Microsoft.Playwright.Program.Main(new[] { "install", "--dry-run", "chromium" });
                return exitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Install Playwright browsers
        /// </summary>
        public static async Task<string> InstallBrowsersAsync()
        {
            try
            {
                // Set longer timeout for download (5 minutes)
                var originalTimeout = Environment.GetEnvironmentVariable("PLAYWRIGHT_DOWNLOAD_CONNECTION_TIMEOUT");
                Environment.SetEnvironmentVariable("PLAYWRIGHT_DOWNLOAD_CONNECTION_TIMEOUT", "300000");
                
                try
                {
                    var exitCode = Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
                    return exitCode == 0 ? "✓ Browsers installed successfully" : "Failed to install browsers (exit code: " + exitCode + ")";
                }
                finally
                {
                    // Restore original value
                    Environment.SetEnvironmentVariable("PLAYWRIGHT_DOWNLOAD_CONNECTION_TIMEOUT", originalTimeout);
                }
            }
            catch (Exception ex)
            {
                return $"Error installing browsers: {ex.Message}";
            }
        }

        /// <summary>
        /// Navigate to URL and return page content
        /// </summary>
        public static async Task<string> BrowseUrlAsync(string argsJson, CancellationToken ct)
        {
            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                var root = doc.RootElement;

                if (!root.TryGetProperty("url", out var urlProp))
                    return JsonSerializer.Serialize(new { error = "Missing 'url' parameter" });

                var url = urlProp.GetString() ?? "";
                var extractText = root.TryGetProperty("extract_text", out var et) && et.GetBoolean();
                var screenshot = root.TryGetProperty("screenshot", out var ss) && ss.GetBoolean();
                var waitForSelector = root.TryGetProperty("wait_for", out var wf) ? wf.GetString() : null;
                var timeoutMs = root.TryGetProperty("timeout_ms", out var tm) ? tm.GetInt32() : 30000;

                if (!await InitializeAsync())
                    return JsonSerializer.Serialize(new { error = $"Failed to initialize browser: {_lastError}" });

                if (_currentPage == null)
                    return JsonSerializer.Serialize(new { error = "No browser page available" });

                // Navigate to URL
                await _currentPage.GotoAsync(url, new PageGotoOptions
                {
                    Timeout = timeoutMs,
                    WaitUntil = WaitUntilState.DOMContentLoaded
                });

                // Wait for selector if specified
                if (!string.IsNullOrEmpty(waitForSelector))
                {
                    await _currentPage.WaitForSelectorAsync(waitForSelector, new PageWaitForSelectorOptions
                    {
                        Timeout = timeoutMs
                    });
                }

                var result = new Dictionary<string, object>
                {
                    ["url"] = _currentPage.Url,
                    ["title"] = await _currentPage.TitleAsync()
                };

                // Extract text content
                if (extractText)
                {
                    var textContent = await _currentPage.EvaluateAsync<string>(@"() => {
                        // Remove scripts, styles, and hidden elements
                        const elementsToRemove = document.querySelectorAll('script, style, noscript, iframe, svg');
                        elementsToRemove.forEach(el => el.remove());
                        
                        // Get text content
                        return document.body.innerText;
                    }");
                    
                    // Clean and truncate text
                    textContent = CleanText(textContent ?? "");
                    if (textContent.Length > 15000)
                        textContent = textContent.Substring(0, 15000) + "\n... [truncated]";
                    
                    result["text"] = textContent;
                }
                else
                {
                    // Get HTML content (truncated)
                    var html = await _currentPage.ContentAsync();
                    if (html.Length > 20000)
                        html = html.Substring(0, 20000) + "\n... [truncated]";
                    result["html"] = html;
                }

                // Take screenshot if requested
                if (screenshot)
                {
                    var screenshotBytes = await _currentPage.ScreenshotAsync(new PageScreenshotOptions
                    {
                        Type = ScreenshotType.Png,
                        FullPage = false
                    });
                    result["screenshot_base64"] = Convert.ToBase64String(screenshotBytes);
                }

                return JsonSerializer.Serialize(result);
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { error = ex.Message });
            }
        }

        /// <summary>
        /// Click an element on the page
        /// </summary>
        public static async Task<string> ClickElementAsync(string argsJson, CancellationToken ct)
        {
            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                var root = doc.RootElement;

                if (!root.TryGetProperty("selector", out var selectorProp))
                    return JsonSerializer.Serialize(new { error = "Missing 'selector' parameter" });

                var selector = selectorProp.GetString() ?? "";
                var timeoutMs = root.TryGetProperty("timeout_ms", out var tm) ? tm.GetInt32() : 10000;

                if (_currentPage == null)
                    return JsonSerializer.Serialize(new { error = "No page loaded. Use browser_navigate first." });

                await _currentPage.ClickAsync(selector, new PageClickOptions { Timeout = timeoutMs });
                
                // Wait for navigation or network idle
                try
                {
                    await _currentPage.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 5000 });
                }
                catch { /* Timeout is ok */ }

                return JsonSerializer.Serialize(new
                {
                    success = true,
                    url = _currentPage.Url,
                    title = await _currentPage.TitleAsync()
                });
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { error = ex.Message });
            }
        }

        /// <summary>
        /// Type text into an element
        /// </summary>
        public static async Task<string> TypeTextAsync(string argsJson, CancellationToken ct)
        {
            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                var root = doc.RootElement;

                if (!root.TryGetProperty("selector", out var selectorProp))
                    return JsonSerializer.Serialize(new { error = "Missing 'selector' parameter" });
                if (!root.TryGetProperty("text", out var textProp))
                    return JsonSerializer.Serialize(new { error = "Missing 'text' parameter" });

                var selector = selectorProp.GetString() ?? "";
                var text = textProp.GetString() ?? "";
                var clear = root.TryGetProperty("clear", out var cl) && cl.GetBoolean();
                var pressEnter = root.TryGetProperty("press_enter", out var pe) && pe.GetBoolean();

                if (_currentPage == null)
                    return JsonSerializer.Serialize(new { error = "No page loaded. Use browser_navigate first." });

                if (clear)
                {
                    await _currentPage.FillAsync(selector, "");
                }

                await _currentPage.FillAsync(selector, text);

                if (pressEnter)
                {
                    await _currentPage.PressAsync(selector, "Enter");
                    try
                    {
                        await _currentPage.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 5000 });
                    }
                    catch { /* Timeout is ok */ }
                }

                return JsonSerializer.Serialize(new { success = true });
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { error = ex.Message });
            }
        }

        /// <summary>
        /// Get elements matching a selector
        /// </summary>
        public static async Task<string> GetElementsAsync(string argsJson, CancellationToken ct)
        {
            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                var root = doc.RootElement;

                if (!root.TryGetProperty("selector", out var selectorProp))
                    return JsonSerializer.Serialize(new { error = "Missing 'selector' parameter" });

                var selector = selectorProp.GetString() ?? "";
                var maxElements = root.TryGetProperty("max", out var m) ? m.GetInt32() : 20;

                if (_currentPage == null)
                    return JsonSerializer.Serialize(new { error = "No page loaded. Use browser_navigate first." });

                var elements = await _currentPage.QuerySelectorAllAsync(selector);
                var results = new List<object>();

                int count = 0;
                foreach (var el in elements)
                {
                    if (count >= maxElements) break;
                    
                    var tagName = await el.EvaluateAsync<string>("e => e.tagName.toLowerCase()");
                    var text = await el.InnerTextAsync();
                    var href = await el.GetAttributeAsync("href");
                    var id = await el.GetAttributeAsync("id");
                    var className = await el.GetAttributeAsync("class");

                    // Truncate long text
                    if (text?.Length > 200)
                        text = text.Substring(0, 200) + "...";

                    results.Add(new
                    {
                        tag = tagName,
                        text = text?.Trim(),
                        href,
                        id,
                        @class = className
                    });
                    count++;
                }

                return JsonSerializer.Serialize(new
                {
                    count = elements.Count,
                    showing = results.Count,
                    elements = results
                });
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { error = ex.Message });
            }
        }

        /// <summary>
        /// Execute JavaScript on the page
        /// </summary>
        public static async Task<string> ExecuteScriptAsync(string argsJson, CancellationToken ct)
        {
            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                var root = doc.RootElement;

                if (!root.TryGetProperty("script", out var scriptProp))
                    return JsonSerializer.Serialize(new { error = "Missing 'script' parameter" });

                var script = scriptProp.GetString() ?? "";

                if (_currentPage == null)
                    return JsonSerializer.Serialize(new { error = "No page loaded. Use browser_navigate first." });

                var result = await _currentPage.EvaluateAsync<object>(script);
                
                return JsonSerializer.Serialize(new
                {
                    success = true,
                    result = result?.ToString()
                });
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { error = ex.Message });
            }
        }

        /// <summary>
        /// Take a screenshot of the current page
        /// </summary>
        public static async Task<string> ScreenshotAsync(string argsJson, CancellationToken ct)
        {
            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                var root = doc.RootElement;

                var fullPage = root.TryGetProperty("full_page", out var fp) && fp.GetBoolean();
                var selector = root.TryGetProperty("selector", out var sel) ? sel.GetString() : null;

                if (_currentPage == null)
                    return JsonSerializer.Serialize(new { error = "No page loaded. Use browser_navigate first." });

                byte[] screenshotBytes;

                if (!string.IsNullOrEmpty(selector))
                {
                    var element = await _currentPage.QuerySelectorAsync(selector);
                    if (element == null)
                        return JsonSerializer.Serialize(new { error = $"Element not found: {selector}" });
                    
                    screenshotBytes = await element.ScreenshotAsync();
                }
                else
                {
                    screenshotBytes = await _currentPage.ScreenshotAsync(new PageScreenshotOptions
                    {
                        Type = ScreenshotType.Png,
                        FullPage = fullPage
                    });
                }

                return JsonSerializer.Serialize(new
                {
                    success = true,
                    screenshot_base64 = Convert.ToBase64String(screenshotBytes),
                    size_bytes = screenshotBytes.Length
                });
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { error = ex.Message });
            }
        }

        /// <summary>
        /// Close the browser
        /// </summary>
        public static async Task<string> CloseBrowserAsync()
        {
            await _lock.WaitAsync();
            try
            {
                if (_currentPage != null)
                {
                    await _currentPage.CloseAsync();
                    _currentPage = null;
                }
                if (_browser != null)
                {
                    await _browser.CloseAsync();
                    _browser = null;
                }
                if (_playwright != null)
                {
                    _playwright.Dispose();
                    _playwright = null;
                }
                _isInitialized = false;
                return JsonSerializer.Serialize(new { success = true, message = "Browser closed" });
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { error = ex.Message });
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Clean up text content from web page
        /// </summary>
        private static string CleanText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "";

            // Remove excessive whitespace
            text = Regex.Replace(text, @"\s+", " ");
            // Remove lines that are just whitespace
            var lines = text.Split('\n')
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrWhiteSpace(l));
            return string.Join("\n", lines);
        }

        public async ValueTask DisposeAsync()
        {
            await CloseBrowserAsync();
        }
    }
}
