using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CodingAgent;
using thuvu.Models;
using thuvu.Tools;

namespace thuvu
{
    /// <summary>
    /// Handlers for slash commands (/diff, /test, /run, /commit, /push, /pull, /config, /set, /rag, /mcp)
    /// </summary>
    public static class CommandHandlers
    {
        // /diff [--staged] [--context N] [--root PATH] [PATH ...]
        public static async Task HandleDiffCommandAsync(string line, CancellationToken ct, Action<string, bool>? outputCallback = null)
        {
            var parts = ConsoleHelpers.TokenizeArgs(line);
            bool staged = false;
            int? context = null;
            string? root = null;
            var paths = new List<string>();

            for (int i = 1; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p.Equals("--staged", StringComparison.OrdinalIgnoreCase)) { staged = true; continue; }
                if (p.Equals("--context", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Count && int.TryParse(parts[i + 1], out var c))
                { context = Math.Clamp(c, 0, 100); i++; continue; }
                if (p.Equals("--root", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Count)
                { root = parts[++i]; continue; }
                if (p.StartsWith("--"))
                {
                    var msg = $"Unknown option: {p}";
                    if (outputCallback != null) outputCallback(msg, true);
                    else Console.WriteLine(msg);
                    continue;
                }
                paths.Add(p);
            }

            var args = new { paths = paths.Count > 0 ? paths : null, staged, context = context ?? 3, root };
            var toolResult = await ToolExecutor.ExecuteToolAsync("git_diff", JsonSerializer.Serialize(args), ct);

            ConsoleHelpers.AutoPrettyPrinterCallback("git_diff", toolResult);

            using var doc = JsonDocument.Parse(toolResult);
            if (doc.RootElement.TryGetProperty("stdout", out var so) && !string.IsNullOrEmpty(so.GetString()))
            {
                var stdout = so.GetString()!;
                if (outputCallback != null) outputCallback(stdout, false);
                else Console.WriteLine(stdout);
            }
            if (doc.RootElement.TryGetProperty("stderr", out var se) && !string.IsNullOrEmpty(se.GetString()))
            {
                var stderr = se.GetString()!;
                if (outputCallback != null) outputCallback(stderr, true);
                else Console.Error.WriteLine(stderr);
            }
        }

        // /test [SOLUTION_OR_PROJECT] [--filter EXP] [--logger trx|console]
        public static async Task HandleTestCommandAsync(string line, CancellationToken ct, Action<string, bool>? outputCallback = null)
        {
            var parts = ConsoleHelpers.TokenizeArgs(line);
            string? slnOrProj = null;
            string? filter = null;
            string? logger = null;

            for (int i = 1; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p.Equals("--filter", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Count)
                { filter = parts[++i]; continue; }
                if (p.Equals("--logger", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Count)
                { logger = parts[++i]; continue; }
                if (p.StartsWith("--")) { Console.WriteLine($"Unknown option: {p}"); continue; }
                if (slnOrProj == null) slnOrProj = p; else Console.WriteLine($"Ignoring extra arg: {p}");
            }

            var args = new { solution_or_project = slnOrProj, filter, logger = string.IsNullOrWhiteSpace(logger) ? "trx" : logger };
            var toolResult = await ToolExecutor.ExecuteToolAsync("dotnet_test", JsonSerializer.Serialize(args), ct);

            ConsoleHelpers.AutoPrettyPrinterCallback("dotnet_test", toolResult);

            using var doc = JsonDocument.Parse(toolResult);
            var stdout = doc.RootElement.GetProperty("stdout").GetString() ?? "";
            var stderr = doc.RootElement.GetProperty("stderr").GetString() ?? "";
            if (!string.IsNullOrWhiteSpace(stdout)) Console.WriteLine(stdout);
            if (!string.IsNullOrWhiteSpace(stderr)) Console.Error.WriteLine(stderr);
        }

        // /run CMD [ARGS ...] [--cwd PATH] [--timeout MS]
        public static async Task HandleRunCommandAsync(string line, CancellationToken ct, Action<string, bool>? outputCallback = null)
        {
            var parts = ConsoleHelpers.TokenizeArgs(line);
            if (parts.Count < 2)
            {
                Console.WriteLine("Usage: /run CMD [ARGS ...] [--cwd PATH] [--timeout MS]");
                return;
            }

            var cmd = parts[1];
            var args = new List<string>();
            string? cwd = null;
            int? timeout = null;

            for (int i = 2; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p.Equals("--cwd", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Count) { cwd = parts[++i]; continue; }
                if (p.Equals("--timeout", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Count && int.TryParse(parts[++i], out var t)) { timeout = Math.Clamp(t, 1000, 600000); continue; }
                args.Add(p);
            }

            if (!RunProcessToolImpl.AllowedCmds.Contains(cmd))
            {
                Console.WriteLine($"Command '{cmd}' not allowed. Allowed: {string.Join(", ", RunProcessToolImpl.AllowedCmds)}");
                return;
            }

            var payload = new { cmd, args = args.ToArray(), cwd, timeout_ms = timeout ?? 120000 };
            var toolResult = await ToolExecutor.ExecuteToolAsync("run_process", JsonSerializer.Serialize(payload), ct);

            ConsoleHelpers.AutoPrettyPrinterCallback("run_process", toolResult);

            using var doc = JsonDocument.Parse(toolResult);
            var stdout = doc.RootElement.GetProperty("stdout").GetString() ?? "";
            var stderr = doc.RootElement.GetProperty("stderr").GetString() ?? "";
            if (!string.IsNullOrWhiteSpace(stdout)) Console.WriteLine(stdout);
            if (!string.IsNullOrWhiteSpace(stderr)) Console.Error.WriteLine(stderr);
        }

        // /commit "message" [--all] [--staged] [--no-test] [--allow-empty] [--root PATH]
        public static async Task HandleCommitCommandAsync(string line, CancellationToken ct)
        {
            var parts = ConsoleHelpers.TokenizeArgs(line);

            if (parts.Count < 2)
            {
                Console.WriteLine("Usage: /commit \"message\" [--all] [--staged] [--no-test] [--allow-empty] [--root PATH]");
                return;
            }

            bool stageAll = false;
            bool useStagedOnly = false;
            bool noTest = false;
            bool allowEmpty = false;
            string? root = null;

            var messageParts = new List<string>();
            for (int i = 1; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p.Equals("--all", StringComparison.OrdinalIgnoreCase)) { stageAll = true; continue; }
                if (p.Equals("--staged", StringComparison.OrdinalIgnoreCase)) { useStagedOnly = true; continue; }
                if (p.Equals("--no-test", StringComparison.OrdinalIgnoreCase)) { noTest = true; continue; }
                if (p.Equals("--allow-empty", StringComparison.OrdinalIgnoreCase)) { allowEmpty = true; continue; }
                if (p.Equals("--root", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Count)
                {
                    root = parts[++i]; continue;
                }
                messageParts.Add(p);
            }

            var message = string.Join(' ', messageParts).Trim('"', ' ').Trim();
            if (string.IsNullOrWhiteSpace(message))
            {
                Console.WriteLine("Commit message required. Example: /commit \"Refactor HttpClient wrapper\"");
                return;
            }

            var startDir = Directory.GetCurrentDirectory();
            var cwd = !string.IsNullOrWhiteSpace(root) ? Path.GetFullPath(root)
                    : SearchFilesToolImpl.DetectProjectRoot(startDir) ?? startDir;

            // 1) Run tests unless skipped
            if (!noTest)
            {
                Console.WriteLine("Running tests before commit...");
                var testPayload = new { cmd = "dotnet", args = new[] { "test", "--logger", "trx" }, cwd, timeout_ms = 600_000 };
                var testJson = await ToolExecutor.ExecuteToolAsync("run_process", JsonSerializer.Serialize(testPayload), ct);

                ConsoleHelpers.AutoPrettyPrinterCallback("dotnet_test", testJson);

                using (var doc = JsonDocument.Parse(testJson))
                {
                    var stdout = doc.RootElement.GetProperty("stdout").GetString() ?? "";
                    var summary = TestSummary.ParseDotnetTestStdout(stdout);
                    if (summary == null)
                    {
                        string? trx = TestSummary.TryFindTrxPathFromStdoutOrFS(stdout, cwd);
                        if (trx != null) summary = TestSummary.ParseTrxSummary(trx);
                    }

                    if (summary == null)
                    {
                        Console.WriteLine("Could not parse test results. Aborting commit. Use --no-test to override.");
                        return;
                    }
                    if (summary.Failed > 0)
                    {
                        Console.WriteLine("❌ Tests failed. Aborting commit. Use --no-test to override.");
                        return;
                    }
                }
            }

            // 2) Stage changes
            if (stageAll && !useStagedOnly)
            {
                Console.WriteLine("Staging all changes...");
                var addPayload = new { cmd = "git", args = new[] { "add", "-A" }, cwd };
                await ToolExecutor.ExecuteToolAsync("run_process", JsonSerializer.Serialize(addPayload), ct);
            }

            // 3) Ensure there is something to commit unless --allow-empty
            if (!allowEmpty)
            {
                var diffPayload = new { cmd = "git", args = new[] { "diff", "--staged", "--name-only" }, cwd };
                var diffJson = await ToolExecutor.ExecuteToolAsync("run_process", JsonSerializer.Serialize(diffPayload), ct);
                using var d = JsonDocument.Parse(diffJson);
                var stagedList = (d.RootElement.GetProperty("stdout").GetString() ?? "").Trim();
                if (string.IsNullOrEmpty(stagedList))
                {
                    Console.WriteLine("No staged changes to commit. Use --all to stage or --allow-empty for an empty commit.");
                    return;
                }
            }

            // 4) Commit
            var commitArgs = new List<string> { "commit", "-m", message };
            if (allowEmpty) commitArgs.Add("--allow-empty");

            Console.WriteLine("Committing...");
            var commitPayload = new { cmd = "git", args = commitArgs.ToArray(), cwd };
            var commitJson = await ToolExecutor.ExecuteToolAsync("run_process", JsonSerializer.Serialize(commitPayload), ct);

            using (var cdoc = JsonDocument.Parse(commitJson))
            {
                var ec = cdoc.RootElement.GetProperty("exit_code").GetInt32();
                var stdout = cdoc.RootElement.GetProperty("stdout").GetString() ?? "";
                var stderr = cdoc.RootElement.GetProperty("stderr").GetString() ?? "";

                if (ec != 0)
                {
                    Console.WriteLine("git commit failed:");
                    if (!string.IsNullOrWhiteSpace(stdout)) Console.WriteLine(stdout);
                    if (!string.IsNullOrWhiteSpace(stderr)) Console.Error.WriteLine(stderr);
                    return;
                }
            }

            // 5) Show short SHA
            var shaPayload = new { cmd = "git", args = new[] { "rev-parse", "--short", "HEAD" }, cwd };
            var shaJson = await ToolExecutor.ExecuteToolAsync("run_process", JsonSerializer.Serialize(shaPayload), ct);
            using (var sdoc = JsonDocument.Parse(shaJson))
            {
                var shortSha = (sdoc.RootElement.GetProperty("stdout").GetString() ?? "").Trim();
                if (!string.IsNullOrEmpty(shortSha))
                {
                    ConsoleHelpers.WithColor(ConsoleColor.Green, () => Console.WriteLine($"✅ Committed as {shortSha} — \"{message}\""));
                }
                else
                {
                    Console.WriteLine($"✅ Commit completed — \"{message}\"");
                }
            }
        }

        // /config show|path|reload|save
        public static Task HandleConfigCommandAsync(string line, HttpClient http, CancellationToken ct)
        {
            var rest = line.Length > 7 ? line[7..].Trim() : "show";
            switch (rest.ToLowerInvariant())
            {
                case "path":
                    Console.WriteLine(AgentConfig.GetConfigPath());
                    break;
                case "show":
                    Console.WriteLine($"HostUrl   : {AgentConfig.Config.HostUrl}");
                    Console.WriteLine($"Model     : {AgentConfig.Config.Model}");
                    Console.WriteLine($"Stream    : {AgentConfig.Config.Stream}");
                    Console.WriteLine($"TimeoutMs : {AgentConfig.Config.TimeoutMs}");
                    Console.WriteLine($"HttpRequestTimeout : {AgentConfig.Config.HttpRequestTimeout} minutes");
                    break;
                case "reload":
                    AgentConfig.LoadConfig();
                    AgentConfig.ApplyConfig(http);
                    Console.WriteLine("Config reloaded.");
                    break;
                case "save":
                    Console.WriteLine(AgentConfig.SaveConfig() ? "Config saved." : "Failed to save config.");
                    break;
                default:
                    Console.WriteLine("Usage: /config [show|path|reload|save]");
                    break;
            }
            return Task.CompletedTask;
        }

        // /set model <id> | /set host <url> | /set stream on|off | /set timeout <ms> | /set httptimeout <minutes>
        public static Task HandleSetCommandAsync(string line, HttpClient http, CancellationToken ct)
        {
            var args = ConsoleHelpers.TokenizeArgs(line);
            if (args.Count < 3)
            {
                Console.WriteLine("Usage: /set model <id> | /set host <url> | /set stream on|off | /set timeout <ms> | /set httptimeout <minutes>");
                return Task.CompletedTask;
            }

            var key = args[1].ToLowerInvariant();
            switch (key)
            {
                case "model":
                    {
                        var id = string.Join(' ', args.Skip(2));
                        if (string.IsNullOrWhiteSpace(id)) { Console.WriteLine("Model id required."); break; }
                        AgentConfig.Config.Model = id.Trim();
                        AgentConfig.SaveConfig();
                        Console.WriteLine($"Model set to: {AgentConfig.Config.Model}");
                        break;
                    }
                case "host":
                    {
                        var url = string.Join(' ', args.Skip(2));
                        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                        {
                            Console.WriteLine("Please provide a valid http(s) URL, e.g. http://127.0.0.1:1234");
                            break;
                        }
                        AgentConfig.Config.HostUrl = uri.ToString().TrimEnd('/');
                        AgentConfig.SaveConfig();
                        Console.WriteLine($"Host set to: {AgentConfig.Config.HostUrl}");
                        Console.WriteLine("Note: Restart the application for the host change to take effect.");
                        break;
                    }
                case "stream":
                    {
                        var v = args[2].ToLowerInvariant();
                        if (v is "on" or "off")
                        {
                            AgentConfig.Config.Stream = v == "on";
                            AgentConfig.SaveConfig();
                            Console.WriteLine($"Streaming is now {(AgentConfig.Config.Stream ? "ON" : "OFF")}.");
                        }
                        else Console.WriteLine("Usage: /set stream on|off");
                        break;
                    }
                case "timeout":
                    {
                        if (!int.TryParse(args[2], out var ms) || ms < 1000 || ms > 600_000)
                        {
                            Console.WriteLine("Timeout must be 1000..600000 ms.");
                            break;
                        }
                        AgentConfig.Config.TimeoutMs = ms;
                        AgentConfig.SaveConfig();
                        Console.WriteLine($"Default process timeout set to {AgentConfig.Config.TimeoutMs} ms.");
                        break;
                    }
                case "httptimeout":
                    {
                        if (!int.TryParse(args[2], out var minutes) || minutes < 1 || minutes > 120)
                        {
                            Console.WriteLine("HTTP timeout must be 1..120 minutes.");
                            break;
                        }
                        AgentConfig.Config.HttpRequestTimeout = minutes;
                        AgentConfig.SaveConfig();
                        Console.WriteLine($"HTTP request timeout set to {AgentConfig.Config.HttpRequestTimeout} minutes.");
                        Console.WriteLine("Note: Restart the application for the timeout change to take effect.");
                        break;
                    }
                default:
                    Console.WriteLine("Supported keys: model, host, stream, timeout, httptimeout");
                    break;
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// Extract TypeScript code block from assistant response and execute it
        /// </summary>
        public static async Task<string?> TryExecuteMcpCodeBlockAsync(string content, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(content)) return null;

            var codeBlockPattern = new Regex(
                @"```(?:typescript|ts|javascript|js)\s*\n([\s\S]*?)```",
                RegexOptions.IgnoreCase);

            var match = codeBlockPattern.Match(content);
            if (!match.Success) return null;

            var code = match.Groups[1].Value.Trim();
            if (string.IsNullOrEmpty(code)) return null;

            // Ask for user approval if configured
            if (McpConfig.Instance.RequireApproval)
            {
                Console.WriteLine();
                ConsoleHelpers.WithColor(ConsoleColor.Yellow, () =>
                {
                    Console.WriteLine("═══════════════════════════════════════════════════════════");
                    Console.WriteLine("  MCP Code Execution Request");
                    Console.WriteLine("═══════════════════════════════════════════════════════════");
                });
                Console.WriteLine();
                ConsoleHelpers.WithColor(ConsoleColor.Cyan, () => Console.WriteLine(code));
                Console.WriteLine();
                Console.WriteLine("Execute this code? [Y]es / [N]o / [A]lways (disable approval)");
                Console.Write("> ");

                var key = Console.ReadKey(intercept: true);
                Console.WriteLine(key.KeyChar);

                if (char.ToUpperInvariant(key.KeyChar) == 'A')
                {
                    McpConfig.Instance.RequireApproval = false;
                    McpConfig.SaveConfig();
                    Console.WriteLine("Approval disabled for this session.");
                }
                else if (char.ToUpperInvariant(key.KeyChar) != 'Y')
                {
                    Console.WriteLine("Execution cancelled.");
                    return "User cancelled code execution.";
                }
            }

            Console.WriteLine();
            ConsoleHelpers.WithColor(ConsoleColor.Blue, () => Console.WriteLine("Executing MCP code..."));

            using var executor = new McpCodeExecutor();
            var result = await executor.ExecuteAsync(code, ct);

            Console.WriteLine();
            if (result.Success)
            {
                ConsoleHelpers.WithColor(ConsoleColor.Green, () => Console.WriteLine($"✓ Execution completed in {result.Duration.TotalMilliseconds:F0}ms"));

                if (!string.IsNullOrEmpty(result.Result))
                {
                    Console.WriteLine();
                    Console.WriteLine("Result:");
                    ConsoleHelpers.WithColor(ConsoleColor.White, () => Console.WriteLine(result.Result));
                }
            }
            else
            {
                ConsoleHelpers.WithColor(ConsoleColor.Red, () => Console.WriteLine($"✗ Execution failed: {result.Error}"));
            }

            if (result.ToolCalls.Count > 0)
            {
                Console.WriteLine();
                ConsoleHelpers.WithColor(ConsoleColor.DarkGray, () =>
                {
                    Console.WriteLine($"Tool calls made: {result.ToolCalls.Count}");
                    foreach (var call in result.ToolCalls)
                    {
                        var icon = call.Success ? "✓" : "✗";
                        Console.WriteLine($"  {icon} {call.ToolName} ({call.Duration.TotalMilliseconds:F0}ms)");
                    }
                });
            }

            Console.WriteLine();

            return result.Success
                ? $"Execution successful. Result: {result.Result ?? "(no return value)"}"
                : $"Execution failed: {result.Error}";
        }
    }
}
