using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using thuvu.Tools.UIAutomation.Models;

namespace thuvu.Tools.UIAutomation.Linux
{
    /// <summary>
    /// Linux implementation of IUIAutomationProvider using xdotool, wmctrl, and import/scrot.
    /// </summary>
    public class LinuxUIProvider : IUIAutomationProvider
    {
        private readonly bool _xdotoolAvailable;
        private readonly bool _wmctrlAvailable;

        public string PlatformName => "Linux";
        public bool IsSupported => _xdotoolAvailable;

        public LinuxUIProvider()
        {
            _xdotoolAvailable = IsToolAvailable("xdotool");
            _wmctrlAvailable = IsToolAvailable("wmctrl");
        }

        private static bool IsToolAvailable(string tool)
        {
            try
            {
                var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "which",
                        Arguments = tool,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                proc.Start();
                proc.WaitForExit(5000);
                return proc.ExitCode == 0;
            }
            catch { return false; }
        }

        #region Screen Capture

        public Task<CaptureResult> CaptureScreenAsync(CaptureOptions options)
            => Task.Run(() => LinuxCapture.CaptureFullScreen(options));

        public Task<CaptureResult> CaptureWindowAsync(string windowTitle, CaptureOptions options)
            => Task.Run(() => LinuxCapture.CaptureWindow(windowTitle, options));

        public Task<CaptureResult> CaptureWindowAsync(IntPtr windowHandle, CaptureOptions options)
            => Task.Run(() =>
            {
                var id = windowHandle.ToInt64().ToString();
                try
                {
                    var tempFile = System.IO.Path.GetTempFileName() + "." + options.Format;
                    var tool = "import";
                    var proc = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = tool,
                            Arguments = $"-window {id} \"{tempFile}\"",
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        }
                    };
                    proc.Start();
                    proc.WaitForExit(30000);

                    if (proc.ExitCode != 0 || !System.IO.File.Exists(tempFile))
                        return new CaptureResult { Success = false, Error = "Window capture failed" };

                    var bytes = System.IO.File.ReadAllBytes(tempFile);
                    var mime = options.Format == "jpeg" ? "image/jpeg" : "image/png";
                    try { System.IO.File.Delete(tempFile); } catch { }

                    return new CaptureResult
                    {
                        Success = true,
                        Base64Data = Convert.ToBase64String(bytes),
                        MimeType = mime,
                        FileSizeBytes = bytes.Length
                    };
                }
                catch (Exception ex)
                {
                    return new CaptureResult { Success = false, Error = ex.Message };
                }
            });

        public Task<CaptureResult> CaptureRegionAsync(int x, int y, int width, int height, CaptureOptions options)
            => Task.Run(() => LinuxCapture.CaptureRegion(x, y, width, height, options));

        #endregion

        #region Window Management

        public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(bool includeHidden = false, string? titleFilter = null)
        {
            return Task.Run<IReadOnlyList<WindowInfo>>(() =>
            {
                var windows = new List<WindowInfo>();

                if (_wmctrlAvailable)
                    windows.AddRange(ListWindowsWmctrl(includeHidden, titleFilter));
                else if (_xdotoolAvailable)
                    windows.AddRange(ListWindowsXdotool(includeHidden, titleFilter));

                return windows.AsReadOnly();
            });
        }

        public Task<WindowInfo?> GetActiveWindowAsync()
        {
            return Task.Run(() =>
            {
                if (!_xdotoolAvailable) return null;
                try
                {
                    var proc = RunXdotool("getactivewindow");
                    var windowId = proc.StandardOutput.ReadToEnd().Trim();
                    proc.WaitForExit(5000);
                    if (string.IsNullOrEmpty(windowId)) return null;
                    return GetWindowInfo(windowId);
                }
                catch { return null; }
            });
        }

        public Task<bool> FocusWindowAsync(string windowTitle)
        {
            return Task.Run(() =>
            {
                if (!_xdotoolAvailable) return false;
                try
                {
                    var id = GetWindowIdByTitle(windowTitle);
                    if (id == null) return false;

                    var proc = RunXdotool($"windowactivate {id}");
                    proc.WaitForExit(5000);
                    return proc.ExitCode == 0;
                }
                catch { return false; }
            });
        }

        public Task<bool> FocusWindowAsync(IntPtr windowHandle)
        {
            return Task.Run(() =>
            {
                if (!_xdotoolAvailable) return false;
                try
                {
                    var proc = RunXdotool($"windowactivate {windowHandle.ToInt64()}");
                    proc.WaitForExit(5000);
                    return proc.ExitCode == 0;
                }
                catch { return false; }
            });
        }

        #endregion

        #region Mouse Input

        public Task<bool> ClickAsync(int x, int y, ClickOptions? options = null)
        {
            return Task.Run(() =>
            {
                if (!_xdotoolAvailable) return false;
                options ??= new ClickOptions();

                try
                {
                    var button = options.Button switch
                    {
                        "right" => 3,
                        "middle" => 2,
                        _ => 1
                    };
                    var clicks = options.Clicks;

                    var absX = x;
                    var absY = y;
                    if (options.WindowRelative && !string.IsNullOrEmpty(options.WindowTitle))
                    {
                        var windowId = GetWindowIdByTitle(options.WindowTitle);
                        if (windowId == null) return false;
                        RunXdotool($"windowactivate {windowId}").WaitForExit(2000);
                        System.Threading.Thread.Sleep(100);
                    }

                    RunXdotool($"mousemove {absX} {absY}").WaitForExit(2000);

                    for (int i = 0; i < clicks; i++)
                    {
                        RunXdotool($"click {button}").WaitForExit(2000);
                        if (i < clicks - 1 && options.DelayMs > 0)
                            System.Threading.Thread.Sleep(options.DelayMs);
                    }
                    return true;
                }
                catch { return false; }
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
            return Task.Run(() =>
            {
                if (!_xdotoolAvailable) return false;
                try
                {
                    RunXdotool($"mousemove {x} {y}").WaitForExit(2000);
                    return true;
                }
                catch { return false; }
            });
        }

        public Task<(int X, int Y)> GetMousePositionAsync()
        {
            return Task.Run(() =>
            {
                if (!_xdotoolAvailable) return (0, 0);
                try
                {
                    var proc = RunXdotool("getmouselocation");
                    var output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(2000);

                    var xMatch = Regex.Match(output, @"x:(\d+)");
                    var yMatch = Regex.Match(output, @"y:(\d+)");
                    var x = xMatch.Success ? int.Parse(xMatch.Groups[1].Value) : 0;
                    var y = yMatch.Success ? int.Parse(yMatch.Groups[1].Value) : 0;
                    return (x, y);
                }
                catch { return (0, 0); }
            });
        }

        #endregion

        #region Keyboard Input

        public Task<bool> TypeTextAsync(string text, TypeOptions? options = null)
        {
            return Task.Run(() =>
            {
                if (!_xdotoolAvailable) return false;
                options ??= new TypeOptions();

                try
                {
                    if (!string.IsNullOrEmpty(options.WindowTitle))
                    {
                        var id = GetWindowIdByTitle(options.WindowTitle);
                        if (id != null)
                        {
                            RunXdotool($"windowactivate {id}").WaitForExit(2000);
                            System.Threading.Thread.Sleep(100);
                        }
                    }

                    var delayArg = options.DelayBetweenKeysMs > 0
                        ? $" --delay {options.DelayBetweenKeysMs}"
                        : "";
                    var escaped = text.Replace("\"", "\\\"");
                    RunXdotool($"type{delayArg} \"{escaped}\"").WaitForExit(10000);
                    return true;
                }
                catch { return false; }
            });
        }

        public Task<bool> SendKeysAsync(string[] keys, TypeOptions? options = null)
        {
            return Task.Run(() =>
            {
                if (!_xdotoolAvailable || keys.Length == 0) return false;
                options ??= new TypeOptions();

                try
                {
                    if (!string.IsNullOrEmpty(options.WindowTitle))
                    {
                        var id = GetWindowIdByTitle(options.WindowTitle);
                        if (id != null)
                        {
                            RunXdotool($"windowactivate {id}").WaitForExit(2000);
                            System.Threading.Thread.Sleep(100);
                        }
                    }

                    var keyCombo = string.Join("+", keys.Select(MapKeyToXdotool));
                    RunXdotool($"key {keyCombo}").WaitForExit(2000);
                    return true;
                }
                catch { return false; }
            });
        }

        #endregion

        #region UI Element Inspection (limited on Linux)

        public Task<UIElement?> GetElementAtAsync(int x, int y)
        {
            return Task.FromResult<UIElement?>(null);
        }

        public Task<IReadOnlyList<UIElement>> FindElementsAsync(string selector)
        {
            return Task.FromResult<IReadOnlyList<UIElement>>(Array.Empty<UIElement>());
        }

        public Task<UIElement?> GetFocusedElementAsync()
        {
            return Task.FromResult<UIElement?>(null);
        }

        #endregion

        public void Dispose() { }

        #region Private Helpers

        private static string? GetWindowIdByTitle(string title)
        {
            try
            {
                var searchTitle = title.ToLowerInvariant();
                var ids = GetAllWindowIds();
                foreach (var id in ids)
                {
                    var name = GetWindowName(id);
                    if (!string.IsNullOrEmpty(name) && name.ToLowerInvariant().Contains(searchTitle))
                        return id;
                }
                return null;
            }
            catch { return null; }
        }

        private static string[] GetAllWindowIds()
        {
            try
            {
                var proc = RunXdotool("search --name .");
                var output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(5000);
                return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToArray();
            }
            catch { return Array.Empty<string>(); }
        }

        private static string? GetWindowName(string windowId)
        {
            try
            {
                var proc = RunXdotool($"getwindowname {windowId}");
                var output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit(5000);
                return string.IsNullOrEmpty(output) ? null : output;
            }
            catch { return null; }
        }

        private static string GetWindowClass(string windowId)
        {
            try
            {
                var proc = RunXdotool($"getwindowclassname {windowId}");
                var output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit(5000);
                return output ?? "";
            }
            catch { return ""; }
        }

        private static int GetWindowPid(string windowId)
        {
            try
            {
                var proc = RunXdotool($"getwindowpid {windowId}");
                var output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit(5000);
                return int.TryParse(output, out var pid) ? pid : 0;
            }
            catch { return 0; }
        }

        private WindowInfo? GetWindowInfo(string windowId)
        {
            try
            {
                var name = GetWindowName(windowId) ?? "";
                var geom = GetWindowGeometry(windowId);
                return new WindowInfo
                {
                    Handle = new IntPtr(long.Parse(windowId)),
                    Title = name,
                    ProcessName = "",
                    ProcessId = GetWindowPid(windowId),
                    X = geom.x,
                    Y = geom.y,
                    Width = geom.w,
                    Height = geom.h,
                    IsVisible = true,
                    ClassName = GetWindowClass(windowId)
                };
            }
            catch { return null; }
        }

        private static (int x, int y, int w, int h) GetWindowGeometry(string windowId)
        {
            try
            {
                var proc = RunXdotool($"getwindowgeometry {windowId}");
                var output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(5000);
                var xMatch = Regex.Match(output, @"Position:\s*(\d+),\s*(\d+)");
                var gMatch = Regex.Match(output, @"Geometry:\s*(\d+)x(\d+)");
                int x = xMatch.Success ? int.Parse(xMatch.Groups[1].Value) : 0;
                int y = xMatch.Success ? int.Parse(xMatch.Groups[2].Value) : 0;
                int w = gMatch.Success ? int.Parse(gMatch.Groups[1].Value) : 0;
                int h = gMatch.Success ? int.Parse(gMatch.Groups[2].Value) : 0;
                return (x, y, w, h);
            }
            catch { return (0, 0, 0, 0); }
        }

        private List<WindowInfo> ListWindowsXdotool(bool includeHidden, string? titleFilter)
        {
            var windows = new List<WindowInfo>();
            try
            {
                var ids = GetAllWindowIds();
                foreach (var id in ids)
                {
                    var name = GetWindowName(id) ?? "";
                    if (string.IsNullOrEmpty(name)) continue;
                    if (!includeHidden && string.IsNullOrEmpty(name)) continue;
                    if (titleFilter != null && !name.ToLowerInvariant().Contains(titleFilter.ToLowerInvariant())) continue;

                    var geom = GetWindowGeometry(id);
                    var pid = GetWindowPid(id);
                    var cls = GetWindowClass(id);

                    windows.Add(new WindowInfo
                    {
                        Handle = long.TryParse(id, out var h) ? new IntPtr(h) : IntPtr.Zero,
                        Title = name,
                        ProcessId = pid,
                        X = geom.x,
                        Y = geom.y,
                        Width = geom.w,
                        Height = geom.h,
                        IsVisible = !string.IsNullOrEmpty(name),
                        ClassName = cls
                    });
                }
            }
            catch { }
            return windows;
        }

        private List<WindowInfo> ListWindowsWmctrl(bool includeHidden, string? titleFilter)
        {
            var windows = new List<WindowInfo>();
            try
            {
                var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "wmctrl",
                        Arguments = "-lG" + (includeHidden ? "x" : ""),
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                proc.Start();
                var output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(5000);

                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = Regex.Split(line.Trim(), @"\s+");
                    if (parts.Length < 8) continue;

                    var title = string.Join(" ", parts.Skip(7));
                    if (titleFilter != null && !title.ToLowerInvariant().Contains(titleFilter.ToLowerInvariant())) continue;

                    if (long.TryParse(parts[0], out var id))
                    {
                        windows.Add(new WindowInfo
                        {
                            Handle = new IntPtr(id),
                            Title = title,
                            X = int.TryParse(parts[2], out var x) ? x : 0,
                            Y = int.TryParse(parts[3], out var y) ? y : 0,
                            Width = int.TryParse(parts[4], out var w) ? w : 0,
                            Height = int.TryParse(parts[5], out var h) ? h : 0,
                            IsVisible = true,
                            ClassName = parts.Length > 6 ? parts[6] : ""
                        });
                    }
                }
            }
            catch { }
            return windows;
        }

        private static Process RunXdotool(string arguments)
        {
            var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "xdotool",
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            return proc;
        }

        private static string MapKeyToXdotool(string key)
        {
            return key.ToLowerInvariant() switch
            {
                "ctrl" => "ctrl",
                "control" => "ctrl",
                "alt" => "alt",
                "shift" => "shift",
                "win" => "super",
                "super" => "super",
                "enter" => "Return",
                "return" => "Return",
                "tab" => "Tab",
                "escape" => "Escape",
                "esc" => "Escape",
                "space" => "space",
                "backspace" => "BackSpace",
                "delete" => "Delete",
                "insert" => "Insert",
                "home" => "Home",
                "end" => "End",
                "pageup" => "Page_Up",
                "pagedown" => "Page_Down",
                "left" => "Left",
                "right" => "Right",
                "up" => "Up",
                "down" => "Down",
                "f1" => "F1", "f2" => "F2", "f3" => "F3", "f4" => "F4",
                "f5" => "F5", "f6" => "F6", "f7" => "F7", "f8" => "F8",
                "f9" => "F9", "f10" => "F10", "f11" => "F11", "f12" => "F12",
                _ => key
            };
        }

        #endregion
    }
}
