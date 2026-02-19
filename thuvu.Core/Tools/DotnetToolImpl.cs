using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace thuvu.Tools
{
    public class DotnetToolImpl
    {
        /// <summary>
        /// Run dotnet restore
        /// </summary>
        public static Task<string> DotnetRestoreTool(string rawArgs)
        {
            try
            {
                var path = ExtractPath(rawArgs);
                var args = new List<string> { "restore" };
                if (!string.IsNullOrWhiteSpace(path)) args.Add(path);
                
                return RunProcessToolImpl.RunProcessToolAsync(JsonSerializer.Serialize(new 
                { 
                    cmd = "dotnet", 
                    args = args.ToArray() 
                }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(JsonSerializer.Serialize(new 
                { 
                    exit_code = -1, 
                    error = ex.Message 
                }));
            }
        }

        /// <summary>
        /// Run dotnet build
        /// </summary>
        public static Task<string> DotnetBuildTool(string rawArgs)
        {
            try
            {
                using var doc = JsonDocument.Parse(rawArgs);
                var root = doc.RootElement;
                var path = ExtractPath(rawArgs);
                var cfg = root.TryGetProperty("configuration", out var c) && c.ValueKind == JsonValueKind.String 
                    ? c.GetString() : "Debug";
                var tfm = root.TryGetProperty("framework", out var f) && f.ValueKind == JsonValueKind.String 
                    ? f.GetString() : null;
                var verbosity = root.TryGetProperty("verbosity", out var v) && v.ValueKind == JsonValueKind.String 
                    ? v.GetString() : null;

                var argList = new List<string> { "build" };
                if (!string.IsNullOrWhiteSpace(path)) argList.Add(path);
                argList.Add("-c");
                argList.Add(cfg ?? "Debug");
                if (!string.IsNullOrWhiteSpace(tfm)) { argList.Add("-f"); argList.Add(tfm); }
                if (!string.IsNullOrWhiteSpace(verbosity)) { argList.Add("-v"); argList.Add(verbosity); }
                
                return RunProcessToolImpl.RunProcessToolAsync(JsonSerializer.Serialize(new 
                { 
                    cmd = "dotnet", 
                    args = argList.ToArray() 
                }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(JsonSerializer.Serialize(new 
                { 
                    exit_code = -1, 
                    error = ex.Message 
                }));
            }
        }

        /// <summary>
        /// Run dotnet test
        /// </summary>
        public static Task<string> DotnetTestTool(string rawArgs)
        {
            try
            {
                using var doc = JsonDocument.Parse(rawArgs);
                var root = doc.RootElement;
                var path = ExtractPath(rawArgs);
                var filter = root.TryGetProperty("filter", out var f) && f.ValueKind == JsonValueKind.String 
                    ? f.GetString() : null;
                var noBuild = root.TryGetProperty("no_build", out var nb) && nb.ValueKind == JsonValueKind.True;
                var verbosity = root.TryGetProperty("verbosity", out var v) && v.ValueKind == JsonValueKind.String 
                    ? v.GetString() : null;

                var args = new List<string> { "test" };
                if (!string.IsNullOrWhiteSpace(path)) args.Add(path);
                args.Add("--logger");
                args.Add("console;verbosity=normal");
                if (!string.IsNullOrWhiteSpace(filter)) { args.Add("--filter"); args.Add(filter); }
                if (noBuild) args.Add("--no-build");
                if (!string.IsNullOrWhiteSpace(verbosity)) { args.Add("-v"); args.Add(verbosity); }
                
                return RunProcessToolImpl.RunProcessToolAsync(JsonSerializer.Serialize(new 
                { 
                    cmd = "dotnet", 
                    args = args.ToArray(),
                    timeout_ms = 300000 // 5 minutes for tests
                }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(JsonSerializer.Serialize(new 
                { 
                    exit_code = -1, 
                    error = ex.Message 
                }));
            }
        }

        /// <summary>
        /// Run dotnet run
        /// </summary>
        public static Task<string> DotnetRunTool(string rawArgs)
        {
            try
            {
                using var doc = JsonDocument.Parse(rawArgs);
                var root = doc.RootElement;
                var path = ExtractPath(rawArgs);
                var cfg = root.TryGetProperty("configuration", out var c) && c.ValueKind == JsonValueKind.String 
                    ? c.GetString() : null;
                var noBuild = root.TryGetProperty("no_build", out var nb) && nb.ValueKind == JsonValueKind.True;
                var timeoutMs = root.TryGetProperty("timeout_ms", out var tm) && tm.ValueKind == JsonValueKind.Number
                    ? tm.GetInt32() : 30000; // 30 seconds default - most console apps should finish quickly
                
                // Get additional arguments to pass to the application
                var appArgs = new List<string>();
                if (root.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var arg in argsEl.EnumerateArray())
                    {
                        if (arg.ValueKind == JsonValueKind.String)
                            appArgs.Add(arg.GetString() ?? "");
                    }
                }

                var args = new List<string> { "run" };
                if (!string.IsNullOrWhiteSpace(path)) { args.Add("--project"); args.Add(path); }
                if (!string.IsNullOrWhiteSpace(cfg)) { args.Add("-c"); args.Add(cfg); }
                if (noBuild) args.Add("--no-build");
                
                // Add -- separator and app arguments if any
                if (appArgs.Count > 0)
                {
                    args.Add("--");
                    args.AddRange(appArgs);
                }
                
                return RunProcessToolImpl.RunProcessToolAsync(JsonSerializer.Serialize(new 
                { 
                    cmd = "dotnet", 
                    args = args.ToArray(),
                    timeout_ms = Math.Clamp(timeoutMs, 1000, 300000)
                }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(JsonSerializer.Serialize(new 
                { 
                    exit_code = -1, 
                    error = ex.Message 
                }));
            }
        }

        /// <summary>
        /// Run dotnet new to create a new project
        /// </summary>
        public static Task<string> DotnetNewTool(string rawArgs)
        {
            try
            {
                using var doc = JsonDocument.Parse(rawArgs);
                var root = doc.RootElement;
                var workDir = thuvu.Models.AgentContext.GetEffectiveWorkDirectory();
                var outputPath = root.TryGetProperty("output", out var o) && o.ValueKind == JsonValueKind.String 
                    ? o.GetString() : null;
                var template = root.TryGetProperty("template", out var t) && t.ValueKind == JsonValueKind.String 
                    ? t.GetString() : null;
                var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String 
                    ? n.GetString() : null;
                var framework = root.TryGetProperty("framework", out var f) && f.ValueKind == JsonValueKind.String 
                    ? f.GetString() : null;

                if (string.IsNullOrWhiteSpace(template))
                {
                    return Task.FromResult(JsonSerializer.Serialize(new 
                    { 
                        exit_code = -1, 
                        error = "template is required" 
                    }));
                }

                var args = new List<string> { "new", template };
                if (!string.IsNullOrWhiteSpace(name)) { args.Add("-n"); args.Add(name); }
                if (!string.IsNullOrWhiteSpace(outputPath)) { args.Add("-o"); args.Add(outputPath); }
                if (!string.IsNullOrWhiteSpace(framework)) { args.Add("-f"); args.Add(framework); }
                
                // Determine working directory
                var cwd = workDir;
                if (!string.IsNullOrWhiteSpace(outputPath))
                {
                    var targetPath = Path.IsPathRooted(outputPath) ? outputPath : Path.Combine(workDir, outputPath);
                    var parentDir = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(parentDir))
                    {
                        Directory.CreateDirectory(parentDir);
                        cwd = parentDir;
                    }
                }
                
                return RunProcessToolImpl.RunProcessToolAsync(JsonSerializer.Serialize(new 
                { 
                    cmd = "dotnet", 
                    args = args.ToArray(), 
                    cwd = cwd 
                }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(JsonSerializer.Serialize(new 
                { 
                    exit_code = -1, 
                    error = ex.Message 
                }));
            }
        }

        /// <summary>
        /// Extract solution or project path from arguments
        /// </summary>
        private static string ExtractPath(string rawArgs)
        {
            try
            {
                using var doc = JsonDocument.Parse(rawArgs);
                return doc.RootElement.TryGetProperty("solution_or_project", out var p) && p.ValueKind == JsonValueKind.String
                    ? (p.GetString() ?? "")
                    : "";
            }
            catch
            {
                return "";
            }
        }
    }
}
