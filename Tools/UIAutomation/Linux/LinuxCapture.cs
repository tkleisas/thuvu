using System;
using System.Diagnostics;
using System.IO;
using thuvu.Tools.UIAutomation.Models;

namespace thuvu.Tools.UIAutomation.Linux
{
    /// <summary>
    /// Linux implementation for screen and window capture using ImageMagick import or scrot.
    /// </summary>
    public static class LinuxCapture
    {
        /// <summary>
        /// Capture the entire screen
        /// </summary>
        public static CaptureResult CaptureFullScreen(CaptureOptions options)
        {
            try
            {
                var tempFile = Path.GetTempFileName() + "." + options.Format;
                var tool = FindCaptureTool();
                if (tool == null)
                    return new CaptureResult { Success = false, Error = "No screenshot tool found. Install ImageMagick (import), scrot, or gnome-screenshot." };

                var (cmd, args) = BuildFullScreenArgs(tool, tempFile);
                var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = cmd,
                        Arguments = args,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                proc.Start();
                proc.WaitForExit(30000);

                if (proc.ExitCode != 0 || !File.Exists(tempFile))
                {
                    var err = proc.StandardError.ReadToEnd();
                    return new CaptureResult { Success = false, Error = $"Screenshot failed: {err}" };
                }

                return BuildResult(tempFile, options);
            }
            catch (Exception ex)
            {
                return new CaptureResult { Success = false, Error = ex.Message };
            }
        }

        /// <summary>
        /// Capture a specific window by title (partial match via xdotool)
        /// </summary>
        public static CaptureResult CaptureWindow(string windowTitle, CaptureOptions options)
        {
            try
            {
                var windowId = GetWindowIdByTitle(windowTitle);
                if (windowId == null)
                    return new CaptureResult { Success = false, Error = $"Window not found: '{windowTitle}'" };

                var tempFile = Path.GetTempFileName() + "." + options.Format;
                var tool = FindCaptureTool();
                if (tool == null)
                    return new CaptureResult { Success = false, Error = "No screenshot tool found." };

                var (cmd, args) = BuildWindowArgs(tool, tempFile, windowId);
                var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = cmd,
                        Arguments = args,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                proc.Start();
                proc.WaitForExit(30000);

                if (proc.ExitCode != 0 || !File.Exists(tempFile))
                    return new CaptureResult { Success = false, Error = $"Window screenshot failed. Exit code: {proc.ExitCode}" };

                var result = BuildResult(tempFile, options);
                result.WindowTitle = windowTitle;
                return result;
            }
            catch (Exception ex)
            {
                return new CaptureResult { Success = false, Error = ex.Message };
            }
        }

        /// <summary>
        /// Capture a rectangular region of the screen
        /// </summary>
        public static CaptureResult CaptureRegion(int x, int y, int width, int height, CaptureOptions options)
        {
            try
            {
                var tempFile = Path.GetTempFileName() + "." + options.Format;
                var tool = FindCaptureTool();
                if (tool == null)
                    return new CaptureResult { Success = false, Error = "No screenshot tool found." };

                // For region capture, use import or scrot
                // import: import -window root -crop WxH+X+Y output.png
                // scrot: scrot -a X,Y,W,H output.png
                var (cmd, args) = tool switch
                {
                    "import" => ("import", $"-window root -crop {width}x{height}+{x}+{y} \"{tempFile}\""),
                    "scrot" => ("scrot", $"-a {x},{y},{width},{height} \"{tempFile}\""),
                    "gnome-screenshot" => ("gnome-screenshot", $"-f \"{tempFile}\""),
                    "maim" => ("maim", $"-g {width}x{height}+{x}+{y} \"{tempFile}\""),
                    _ => ("import", $"-window root -crop {width}x{height}+{x}+{y} \"{tempFile}\"")
                };

                var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = cmd,
                        Arguments = args,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                proc.Start();
                proc.WaitForExit(30000);

                if (proc.ExitCode != 0 || !File.Exists(tempFile))
                    return new CaptureResult { Success = false, Error = $"Region screenshot failed. Exit code: {proc.ExitCode}" };

                return BuildResult(tempFile, options);
            }
            catch (Exception ex)
            {
                return new CaptureResult { Success = false, Error = ex.Message };
            }
        }

        private static string? GetWindowIdByTitle(string title)
        {
            try
            {
                var searchTitle = title.ToLowerInvariant();
                var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "xdotool",
                        Arguments = "search --name .",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                proc.Start();
                var output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(5000);

                if (string.IsNullOrWhiteSpace(output)) return null;

                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var id = line.Trim();
                    var nameProc = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = "xdotool",
                            Arguments = $"getwindowname {id}",
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        }
                    };
                    nameProc.Start();
                    var name = nameProc.StandardOutput.ReadToEnd().Trim();
                    nameProc.WaitForExit(5000);

                    if (!string.IsNullOrEmpty(name) && name.ToLowerInvariant().Contains(searchTitle))
                        return id;
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        private static string? FindCaptureTool()
        {
            string[] tools = { "import", "scrot", "gnome-screenshot", "maim" };
            foreach (var tool in tools)
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
                    if (proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(proc.StandardOutput.ReadToEnd()))
                        return tool;
                }
                catch { }
            }
            return null;
        }

        private static (string cmd, string args) BuildFullScreenArgs(string tool, string outputFile)
        {
            return tool switch
            {
                "import" => ("import", $"-window root \"{outputFile}\""),
                "scrot" => ("scrot", $"\"{outputFile}\""),
                "gnome-screenshot" => ("gnome-screenshot", $"-f \"{outputFile}\""),
                "maim" => ("maim", $"\"{outputFile}\""),
                _ => ("import", $"-window root \"{outputFile}\"")
            };
        }

        private static (string cmd, string args) BuildWindowArgs(string tool, string outputFile, string windowId)
        {
            return tool switch
            {
                "import" => ("import", $"-window {windowId} \"{outputFile}\""),
                "scrot" => ("scrot", $"-u -o \"{outputFile}\""),
                "gnome-screenshot" => ("gnome-screenshot", $"-w -f \"{outputFile}\""),
                "maim" => ("maim", $"-i {windowId} \"{outputFile}\""),
                _ => ("import", $"-window {windowId} \"{outputFile}\"")
            };
        }

        private static CaptureResult BuildResult(string tempFile, CaptureOptions options)
        {
            try
            {
                var bytes = File.ReadAllBytes(tempFile);
                var base64 = Convert.ToBase64String(bytes);
                var mime = options.Format.ToLowerInvariant() == "jpeg" ? "image/jpeg" : "image/png";

                if (options.Output == "file" && !string.IsNullOrEmpty(options.FilePath))
                {
                    File.Copy(tempFile, options.FilePath, overwrite: true);
                    try { File.Delete(tempFile); } catch { }
                    return new CaptureResult
                    {
                        Success = true,
                        FilePath = options.FilePath,
                        Base64Data = base64,
                        MimeType = mime,
                        FileSizeBytes = new FileInfo(options.FilePath).Length
                    };
                }

                var result = new CaptureResult
                {
                    Success = true,
                    Base64Data = base64,
                    MimeType = mime,
                    FileSizeBytes = bytes.Length
                };

                try { File.Delete(tempFile); } catch { }
                return result;
            }
            catch (Exception ex)
            {
                try { File.Delete(tempFile); } catch { }
                return new CaptureResult { Success = false, Error = ex.Message };
            }
        }
    }
}
