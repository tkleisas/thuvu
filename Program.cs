using CodingAgent;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using thuvu.Models;
using thuvu.Tools;

namespace thuvu
{
    internal class Program
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
        public static string anim = "/-\\|";
        //public static int anim_idx=0;
        //private static readonly Uri BaseUri = new("http://127.0.0.1:1234"); // LM Studio default
        private const string DefaultModel = "qwen/qwen3-4b-2507";
        //private static bool _streamResponses = true; // default: streaming on
        private static int _currentContextLength = 0;

        public static async Task Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine(AppContext.BaseDirectory);
            var model = DefaultModel;
            Models.AgentConfig.LoadConfig();

            // Check if TUI mode is requested
            bool useTui = args.Length > 0 && args[0].Equals("--tui", StringComparison.OrdinalIgnoreCase);
            useTui = true;
            // Initialize permission manager with current directory
            Models.PermissionManager.SetCurrentRepoPath(Directory.GetCurrentDirectory());

            using var http = new HttpClient();
            AgentConfig.ApplyConfig(http);
            try
            {
                _currentContextLength = (int)(await GetContextLengthAsync(http, AgentConfig.Config.Model, CancellationToken.None) ?? 4096);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Could not connect to LLM service ({ex.Message}). Using default context length.");
                _currentContextLength = 4096;
            }
            // Conversation state
            var messages = new List<ChatMessage>
            {
                new(
                    "system",
                    "You are a helpful coding agent.Prefer tools over guessing;"+
                    " never invent file paths. When modifying code: "+
                    "(1) read_file, "+
                    "(2) propose a minimal unified diff via apply_patch, "+
                    "(3) run dotnet_build and dotnet_test, "+
                    "(4) if green, you may git_commit with a concise message. " +
                    "If write_file returns checksum_mismatch, re-read the file and rebase your patch."+
                    "Use search_files before claiming a symbol/file doesn’t exist." +
                    "Emit 'thuvu Finished Tasks' when you have completed all your tasks.")
            };

            // Tools you expose to the model
            var tools = BuildTools.GetBuildTools();
            
            if (useTui)
            {
                // Use Terminal.GUI interface
                var tuiInterface = new TuiInterface(http, tools, messages);
                tuiInterface.Run();
                return;
            }

            // Original console interface
            Console.WriteLine("T.H.U.V.U. coding agent (C) 2025 "+Helpers.GetCurrentGitTag());
            Console.WriteLine("type /exit to quit, /help for full list of commands, or --tui for Terminal UI");
            Console.WriteLine($"Config file: {AgentConfig.GetConfigPath()}");
            Console.WriteLine($"Model: {AgentConfig.Config.Model}");
            Console.WriteLine($"Host:  {AgentConfig.Config.HostUrl}");
            var streamingStatus = AgentConfig.Config.Stream ? "ON" : "OFF";
            Console.WriteLine($"Streaming responses: {streamingStatus}");

            Console.WriteLine();

            while (true)
            {
                Console.Write("> ");
                var user = Console.ReadLine();
                if (user == null) continue;

                // Commands
                if (user.Equals("/exit", StringComparison.OrdinalIgnoreCase)) 
                {
                    Models.PermissionManager.ClearSessionPermissions();
                    break;
                }

                if (user.Equals("/test-permissions", StringComparison.OrdinalIgnoreCase))
                {
                    PermissionSystemDemo.RunDemo();
                    continue;
                }

                if (user.StartsWith("/clear", StringComparison.OrdinalIgnoreCase))
                {
                    messages = new List<ChatMessage> { new("system", messages[0].Content) };
                    Console.WriteLine("Conversation cleared.");
                    continue;
                }

                if (user.StartsWith("/system ", StringComparison.OrdinalIgnoreCase))
                {
                    var sys = user.Substring(8).Trim();
                    if (string.IsNullOrWhiteSpace(sys))
                    {
                        Console.WriteLine("Usage: /system <text>");
                        continue;
                    }
                    messages[0] = new ChatMessage("system", sys);
                    Console.WriteLine("System prompt updated.");
                    continue;
                }
                if (user.StartsWith("/stream", StringComparison.OrdinalIgnoreCase))
                {
                    var arg = user.Length > 7 ? user[7..].Trim() : "";
                    if (string.Equals(arg, "on", StringComparison.OrdinalIgnoreCase)) AgentConfig.Config.StreamConfig = true;
                    else if (string.Equals(arg, "off", StringComparison.OrdinalIgnoreCase)) AgentConfig.Config.StreamConfig = false;
                    else
                    {
                        Console.WriteLine("Usage: /stream on|off");
                        continue;
                    }

                    Console.WriteLine($"Streaming is now {(AgentConfig.Config.StreamConfig ? "ON" : "OFF")}.");
                    continue;
                }
                if (user.Equals("/help", StringComparison.OrdinalIgnoreCase))
                {
                    PrintHelp();
                    continue;
                }

                if (user.StartsWith("/diff", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleDiffCommandAsync(user, CancellationToken.None, null);
                    continue;
                }

                if (user.StartsWith("/test", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleTestCommandAsync(user, CancellationToken.None, null);
                    continue;
                }

                if (user.StartsWith("/run", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleRunCommandAsync(user, CancellationToken.None, null);
                    continue;
                }

                if (user.StartsWith("/commit", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleCommitCommandAsync(user, CancellationToken.None);
                    continue;
                }
                if (user.StartsWith("/push", StringComparison.OrdinalIgnoreCase))
                {
                    await HandlePushCommandAsync(user, CancellationToken.None);
                    continue;
                }
                if (user.StartsWith("/pull", StringComparison.OrdinalIgnoreCase))
                {
                    await HandlePullCommandAsync(user, CancellationToken.None);
                    continue;
                }
                if (user.Equals("/config", StringComparison.OrdinalIgnoreCase) ||
                    user.StartsWith("/config ", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleConfigCommandAsync(user, http, CancellationToken.None);
                    continue;
                }

                if (user.StartsWith("/set ", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleSetCommandAsync(user, http, CancellationToken.None);
                    continue;
                }



                if (string.IsNullOrWhiteSpace(user)) continue;

                messages.Add(new ChatMessage("user", user));

                // Send to LM Studio and run tool loop until final answer
                string? final;

                if (AgentConfig.Config.StreamConfig)
                {
                    final = await CompleteWithToolsStreamingAsync(
                        http, AgentConfig.Config.Model, messages, tools, CancellationToken.None,
                        onToken: token => Console.Write(token),
                        onToolResult: AutoPrettyPrinterCallback,   // <— NEW
                        onUsage: u => Console.WriteLine($"\n[tokens] prompt={u.PromptTokens}, completion={u.CompletionTokens}, total={u.TotalTokens}")  // <— NEW
                    );
                    Console.WriteLine();
                }
                else
                {
                    final = await CompleteWithToolsAsync(
                        http, AgentConfig.Config.Model, messages, tools, CancellationToken.None,
                        onToolResult: AutoPrettyPrinterCallback  // <— NEW
                        
                    );
                }

                if (!string.IsNullOrEmpty(final))
                {
                    Console.WriteLine();
                    Console.WriteLine(final);
                    Console.WriteLine();
                    messages.Add(new ChatMessage("assistant", final));
                }
                else
                {
                    Console.WriteLine("(no content)");
                }

            }
        }



        /// <summary>
        /// Sends the current conversation to LM Studio. If the assistant requests tools,
        /// executes them, appends tool results, and repeats until a final answer is produced.
        /// Returns the assistant's final content.
        /// </summary>
        public static async Task<string?> CompleteWithToolsAsync(
            HttpClient http,
            string model,
            List<ChatMessage> messages,
            List<Tool> tools,
            CancellationToken ct,
            Action<string, string>? onToolResult = null) // <— NEW
        {
            while (true)
            {
                var req = new ChatRequest
                {
                    Model = model,
                    Messages = messages,
                    Tools = tools,
                    ToolChoice = "auto",
                    Temperature = 0.2
                };

                using var resp = await http.PostAsJsonAsync("/v1/chat/completions", req, JsonOpts, ct);
                resp.EnsureSuccessStatusCode();

                var body = await resp.Content.ReadFromJsonAsync<ChatResponse>(JsonOpts, ct)
                           ?? throw new InvalidOperationException("Empty response.");
                if(body?.Usage is { } u)
                    Console.WriteLine($"[tokens] prompt={u.PromptTokens}, completion={u.CompletionTokens}, total={u.TotalTokens}");
                var msg = body.Choices[0].Message;

                if (msg.ToolCalls is { Count: > 0 })
                {
                    messages.Add(msg);

                    foreach (var call in msg.ToolCalls)
                    {
                        var name = call.Function.Name;
                        var argsJson = call.Function.Arguments ?? "{}";
                        var toolResult = await ExecuteToolAsync(name, argsJson, ct);
                        Console.WriteLine($"[tool] {name}({argsJson}) => {toolResult}");
                        // NEW: auto pretty print callback
                        onToolResult?.Invoke(name, toolResult);

                        messages.Add(new ChatMessage(
                            role: "tool",
                            content: toolResult,
                            name: name,
                            toolCallId: call.Id
                        ));
                    }

                    continue;
                }

                return msg.Content;
            }
        }


        /// Like your CompleteWithToolsAsync, but streams tokens for final answers.
        /// Prints tokens as they arrive via onToken (e.g., Console.Write).
        public static async Task<string?> CompleteWithToolsStreamingAsync(
            HttpClient http,
            string model,
            List<ChatMessage> messages,
            List<Tool> tools,
            CancellationToken ct,
            Action<string>? onToken = null,
            Action<string, string>? onToolResult = null,
            Action<Usage>? onUsage = null) // <— NEW
        {
            while (true)
            {
                var req = new ChatRequest
                {
                    Model = model,
                    Messages = messages,
                    Tools = tools,
                    ToolChoice = "auto",
                    Temperature = 0.2
                };

                var result = await StreamResult.StreamChatOnceAsync(http, req, ct, onToken);
                //Console.WriteLine("\r" + anim.Substring(anim_idx++,1));
                //anim_idx = anim_idx % anim.Length;
                if (result.ToolCalls is { Count: > 0 })
                {
                    messages.Add(new ChatMessage
                    {
                        Role = "assistant",
                        Content = null,
                        ToolCalls = result.ToolCalls
                    });

                    foreach (var call in result.ToolCalls)
                    {
                        var name = call.Function.Name;
                        var argsJson = call.Function.Arguments ?? "{}";
                        var toolResult = await ExecuteToolAsync(name, argsJson, ct);
                        Console.WriteLine($"[tool] {name}({argsJson}) => {toolResult}");
                        // NEW: auto pretty print callback
                        onToolResult?.Invoke(name, toolResult);

                        messages.Add(new ChatMessage(
                            role: "tool",
                            content: toolResult,
                            name: name,
                            toolCallId: call.Id
                        ));
                    }

                    continue;
                }

                return result.Content;
            }
        }

        private static void PrintUnifiedDiff(string patch)
        {
            if (string.IsNullOrEmpty(patch))
            {
                Console.WriteLine("(no diff)");
                return;
            }

            var lines = patch.Replace("\r\n", "\n").Split('\n');
            foreach (var raw in lines)
            {
                var line = raw;

                if (line.StartsWith("diff --git "))
                {
                    WithColor(ConsoleColor.Yellow, () => Console.WriteLine(line));
                }
                else if (line.StartsWith("--- ") || line.StartsWith("+++ "))
                {
                    WithColor(ConsoleColor.Cyan, () => Console.WriteLine(line));
                }
                else if (line.StartsWith("@@"))
                {
                    WithColor(ConsoleColor.Magenta, () => Console.WriteLine(line));
                }
                else if (line.StartsWith("+") && !line.StartsWith("+++ "))
                {
                    WithColor(ConsoleColor.Green, () => Console.WriteLine(line));
                }
                else if (line.StartsWith("-") && !line.StartsWith("--- "))
                {
                    WithColor(ConsoleColor.Red, () => Console.WriteLine(line));
                }
                else
                {
                    Console.WriteLine(line);
                }
            }

            static void WithColor(ConsoleColor color, Action act)
            {
                var prev = Console.ForegroundColor;
                try { Console.ForegroundColor = color; act(); }
                finally { Console.ForegroundColor = prev; }
            }
        }

        /// <summary>
        /// Executes a tool by name, returning a JSON string result.
        /// </summary>
        private static async Task<string> ExecuteToolAsync(string name, string argsJson, CancellationToken ct)
        {
            try
            {
                // Check permissions before executing
                if (!Models.PermissionManager.CheckPermission(name, argsJson))
                {
                    return JsonSerializer.Serialize(new { error = "Permission denied by user" });
                }
                switch (name)
                {
                    // Navigation / IO
                    case "search_files":
                        return JsonSerializer.Serialize(new { matches = SearchFilesToolImpl.SearchFilesTool(argsJson) });

                    case "read_file":
                        return ReadFileToolImpl.ReadFileTool(argsJson);

                    case "write_file":
                        return WriteFileToolImpl.WriteFileTool(argsJson);

                    case "apply_patch":
                        return ApplyPatchToolImpl.ApplyPatchTool(argsJson);

                    // Process runner
                    case "run_process":
                        return await RunProcessToolImpl.RunProcessToolAsync(argsJson);

                    // dotnet
                    case "dotnet_restore":
                        return await DotnetToolImpl.DotnetRestoreTool(argsJson);

                    case "dotnet_build":
                        return await DotnetToolImpl.DotnetBuildTool(argsJson);

                    case "dotnet_test":
                        return await DotnetToolImpl.DotnetTestTool(argsJson);
                    case "dotnet_run":
                        return await DotnetToolImpl.DotnetRunTool(argsJson);
                    case "dotnet_new":
                        return await DotnetToolImpl.DotnetNewTool(argsJson);
                    // git
                    case "git_status":
                        return await RunProcessToolImpl.RunProcessToolAsync(JsonSerializer.Serialize(Helpers.BuildGitStatusArgs(argsJson)));

                    case "git_diff":
                        return await RunProcessToolImpl.RunProcessToolAsync(JsonSerializer.Serialize(Helpers.BuildGitDiffArgs(argsJson)));

                    // NuGet
                    case "nuget_search":
                        return await RunProcessToolImpl.RunProcessToolAsync(JsonSerializer.Serialize(new
                        {
                            cmd = "dotnet",
                            args = new[] { "nuget", "search", Helpers.ExtractQuery(argsJson) }
                        }));

                    case "nuget_add":
                        return await RunProcessToolImpl.RunProcessToolAsync(JsonSerializer.Serialize(Helpers.BuildNugetAddArgs(argsJson)));

                    default:
                        return JsonSerializer.Serialize(new { error = $"Unknown tool: {name}" });
                }
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { error = ex.Message });
            }
        }

        private static void AutoPrettyPrinterCallback(string toolName, string toolResultJson)
        {
            try
            {
                using var doc = JsonDocument.Parse(toolResultJson);
                var root = doc.RootElement;

                // 1) Look for stdout-bearing tools (run_process, git_*, dotnet_*)
                if (root.TryGetProperty("stdout", out var stdoutEl) && stdoutEl.ValueKind == JsonValueKind.String)
                {
                    var stdout = stdoutEl.GetString() ?? "";

                    // A) Diffs: git_diff or anything that *looks* like a unified diff
                    if (toolName == "git_diff" || LooksLikeUnifiedDiff(stdout))
                    {
                        Console.WriteLine();
                        PrintUnifiedDiff(stdout);
                        Console.WriteLine();
                        return;
                    }

                    // B) Tests: dotnet_test or any output that looks like a test summary
                    if (toolName == "dotnet_test" ||
                        stdout.IndexOf("Failed:", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        stdout.IndexOf("Passed!", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        stdout.IndexOf("Failed!", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var summary = TestSummary.ParseDotnetTestStdout(stdout);
                        if (summary == null)
                        {
                            var trx = TestSummary.TryFindTrxPathFromStdoutOrFS(stdout);
                            if (trx != null) summary = TestSummary.ParseTrxSummary(trx);
                        }
                        if (summary != null)
                        {
                            Console.WriteLine();
                            TestSummary.PrintTestSummary(summary);
                            Console.WriteLine();
                            return;
                        }
                    }
                }
            }
            catch
            {
                // Non-fatal: never let UI helpers crash the agent loop
            }
        }

        private static bool LooksLikeUnifiedDiff(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var lines = text.Replace("\r\n", "\n").Split('\n');

            // Strong signals
            if (lines.Any(l => l.StartsWith("diff --git ", StringComparison.Ordinal))) return true;

            // Hunk markers plus +/- lines
            int hunks = lines.Count(l => l.StartsWith("@@"));
            if (hunks > 0)
            {
                int adds = lines.Count(l => l.StartsWith("+") && !l.StartsWith("+++ "));
                int dels = lines.Count(l => l.StartsWith("-") && !l.StartsWith("--- "));
                if (adds + dels > 0) return true;
            }

            // File headers only
            if (lines.Any(l => l.StartsWith("--- ")) && lines.Any(l => l.StartsWith("+++ "))) return true;

            return false;
        }
        // Split a command line into tokens, respecting quotes.
        public static List<string> TokenizeArgs(string text)
        {
            var res = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return res;

            var sb = new StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"') { inQuotes = !inQuotes; continue; }
                if (char.IsWhiteSpace(c) && !inQuotes)
                {
                    if (sb.Length > 0) { res.Add(sb.ToString()); sb.Clear(); }
                }
                else sb.Append(c);
            }
            if (sb.Length > 0) res.Add(sb.ToString());
            return res;
        }
        // /diff [--staged] [--context N] [--root PATH] [PATH ...]
        public static async Task HandleDiffCommandAsync(string line, CancellationToken ct, Action<string, bool>? outputCallback = null)
        {
            var parts = TokenizeArgs(line);
            // parts[0] == "/diff"
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
                if (p.StartsWith("--")) { 
                    var msg = $"Unknown option: {p}";
                    if (outputCallback != null) 
                        outputCallback(msg, true);
                    else 
                        Console.WriteLine(msg);
                    continue; 
                }
                paths.Add(p);
            }

            var args = new { paths = paths.Count > 0 ? paths : null, staged, context = context ?? 3, root };
            var toolResult = await ExecuteToolAsync("git_diff", JsonSerializer.Serialize(args), ct);

            // Auto-pretty-print diff (and fall back to raw if not recognized)
            AutoPrettyPrinterCallback("git_diff", toolResult);

            // Also print raw stdout/stderr if present
            using var doc = JsonDocument.Parse(toolResult);
            if (doc.RootElement.TryGetProperty("stdout", out var so) && !string.IsNullOrEmpty(so.GetString()))
            {
                var stdout = so.GetString()!;
                if (outputCallback != null) 
                    outputCallback(stdout, false);
                else 
                    Console.WriteLine(stdout);
            }
            if (doc.RootElement.TryGetProperty("stderr", out var se) && !string.IsNullOrEmpty(se.GetString()))
            {
                var stderr = se.GetString()!;
                if (outputCallback != null) 
                    outputCallback(stderr, true);
                else 
                    Console.Error.WriteLine(stderr);
            }
        }

        // /test [SOLUTION_OR_PROJECT] [--filter EXP] [--logger trx|console]
        public static async Task HandleTestCommandAsync(string line, CancellationToken ct, Action<string, bool>? outputCallback = null)
        {
            var parts = TokenizeArgs(line);
            // parts[0] == "/test"
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
            var toolResult = await ExecuteToolAsync("dotnet_test", JsonSerializer.Serialize(args), ct);

            // Auto-pretty-print summary
            AutoPrettyPrinterCallback("dotnet_test", toolResult);

            using var doc = JsonDocument.Parse(toolResult);
            var stdout = doc.RootElement.GetProperty("stdout").GetString() ?? "";
            var stderr = doc.RootElement.GetProperty("stderr").GetString() ?? "";
            if (!string.IsNullOrWhiteSpace(stdout)) Console.WriteLine(stdout);
            if (!string.IsNullOrWhiteSpace(stderr)) Console.Error.WriteLine(stderr);
        }



        public static async Task HandleRunCommandAsync(string line, CancellationToken ct, Action<string, bool>? outputCallback = null)
        {
            var parts = TokenizeArgs(line);
            // parts[0] == "/run"
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

            // Respect whitelist from your RunProcessToolAsync
            if (!RunProcessToolImpl.AllowedCmds.Contains(cmd))
            {
                Console.WriteLine($"Command '{cmd}' not allowed. Allowed: {string.Join(", ", RunProcessToolImpl.AllowedCmds)}");
                return;
            }

            var payload = new { cmd, args = args.ToArray(), cwd, timeout_ms = timeout ?? 120000 };
            var toolResult = await ExecuteToolAsync("run_process", JsonSerializer.Serialize(payload), ct);

            // Pretty-print if it looks like a diff or tests; otherwise dump stdout/stderr
            AutoPrettyPrinterCallback("run_process", toolResult);

            using var doc = JsonDocument.Parse(toolResult);
            var stdout = doc.RootElement.GetProperty("stdout").GetString() ?? "";
            var stderr = doc.RootElement.GetProperty("stderr").GetString() ?? "";
            if (!string.IsNullOrWhiteSpace(stdout)) Console.WriteLine(stdout);
            if (!string.IsNullOrWhiteSpace(stderr)) Console.Error.WriteLine(stderr);
        }
        private static async Task HandleCommitCommandAsync(string line, CancellationToken ct)
        {
            var parts = TokenizeArgs(line); // you already have this helper
                                            // parts[0] == "/commit"

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
                // non-option -> part of message
                messageParts.Add(p);
            }

            // Commit message may be multiple tokens (quoted or not)
            var message = string.Join(' ', messageParts).Trim('"', ' ').Trim();
            if (string.IsNullOrWhiteSpace(message))
            {
                Console.WriteLine("Commit message required. Example: /commit \"Refactor HttpClient wrapper\"");
                return;
            }

            // Resolve working directory
            var startDir = Directory.GetCurrentDirectory();
            var cwd = !string.IsNullOrWhiteSpace(root) ? Path.GetFullPath(root)
                    : SearchFilesToolImpl.DetectProjectRoot(startDir) ?? startDir; // you already have DetectProjectRoot

            // 1) Run tests unless skipped
            if (!noTest)
            {
                Console.WriteLine("Running tests before commit...");
                var testPayload = new { cmd = "dotnet", args = new[] { "test", "--logger", "trx" }, cwd, timeout_ms = 600_000 };
                var testJson = await ExecuteToolAsync("run_process", JsonSerializer.Serialize(testPayload), ct);

                // Pretty summary (uses your existing utilities)
                AutoPrettyPrinterCallback("dotnet_test", testJson);

                using (var doc = JsonDocument.Parse(testJson))
                {
                    var stdout = doc.RootElement.GetProperty("stdout").GetString() ?? "";
                    var summary = TestSummary.ParseDotnetTestStdout(stdout);
                    if(summary == null)
                    {
                        string? trx = TestSummary.TryFindTrxPathFromStdoutOrFS(stdout, cwd);
                        if(trx!=null)
                        {
                            summary = TestSummary.ParseTrxSummary(trx);
                        }
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
                var addJson = await ExecuteToolAsync("run_process", JsonSerializer.Serialize(addPayload), ct);
                // optional: check stderr
            }

            // 3) Ensure there is something to commit unless --allow-empty
            if (!allowEmpty)
            {
                var diffPayload = new { cmd = "git", args = new[] { "diff", "--staged", "--name-only" }, cwd };
                var diffJson = await ExecuteToolAsync("run_process", JsonSerializer.Serialize(diffPayload), ct);
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
            var commitJson = await ExecuteToolAsync("run_process", JsonSerializer.Serialize(commitPayload), ct);

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
            var shaJson = await ExecuteToolAsync("run_process", JsonSerializer.Serialize(shaPayload), ct);
            using (var sdoc = JsonDocument.Parse(shaJson))
            {
                var shortSha = (sdoc.RootElement.GetProperty("stdout").GetString() ?? "").Trim();
                if (!string.IsNullOrEmpty(shortSha))
                {
                    var prev = Console.ForegroundColor;
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"✅ Committed as {shortSha} — \"{message}\"");
                    Console.ForegroundColor = prev;
                }
                else
                {
                    Console.WriteLine($"✅ Commit completed — \"{message}\"");
                }
            }
        }
        // /push [--remote NAME] [--branch NAME] [--set-upstream] [--force-with-lease] [--tags] [--dry-run] [--allow-behind] [--root PATH]
        private static async Task HandlePushCommandAsync(string line, CancellationToken ct)
        {
            var parts = TokenizeArgs(line);
            // parts[0] == "/push"

            string? remote = null;
            string? branch = null;
            bool setUpstream = false;
            bool forceWithLease = false;
            bool pushTags = false;
            bool dryRun = false;
            bool allowBehind = false;
            string? root = null;

            for (int i = 1; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p.Equals("--remote", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Count) { remote = parts[++i]; continue; }
                if (p.Equals("--branch", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Count) { branch = parts[++i]; continue; }
                if (p.Equals("--set-upstream", StringComparison.OrdinalIgnoreCase)) { setUpstream = true; continue; }
                if (p.Equals("--force-with-lease", StringComparison.OrdinalIgnoreCase)) { forceWithLease = true; continue; }
                if (p.Equals("--tags", StringComparison.OrdinalIgnoreCase)) { pushTags = true; continue; }
                if (p.Equals("--dry-run", StringComparison.OrdinalIgnoreCase)) { dryRun = true; continue; }
                if (p.Equals("--allow-behind", StringComparison.OrdinalIgnoreCase)) { allowBehind = true; continue; }
                if (p.Equals("--root", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Count) { root = parts[++i]; continue; }
                Console.WriteLine($"Unknown option: {p}");
            }

            var startDir = Directory.GetCurrentDirectory();
            var cwd = !string.IsNullOrWhiteSpace(root) ? Path.GetFullPath(root)
                    : SearchFilesToolImpl.DetectProjectRoot(startDir) ?? startDir;

            // 1) Determine current branch if not provided
            if (string.IsNullOrWhiteSpace(branch))
            {
                branch = await GitRevParseAsync("HEAD", "--abbrev-ref", cwd, ct);
                if (string.IsNullOrWhiteSpace(branch))
                {
                    Console.WriteLine("Could not determine current branch. Are you in a git repo?");
                    return;
                }
            }

            // 2) Check upstream (may not exist on new branches)
            var upstream = await GitRevParseAsync("@{u}", "--abbrev-ref", cwd, ct);
            bool hasUpstream = !string.IsNullOrWhiteSpace(upstream);

            if (!hasUpstream && !setUpstream)
            {
                Console.WriteLine($"No upstream configured for '{branch}'.");
                Console.WriteLine("Re-run with: /push --set-upstream [--remote origin] [--branch " + branch + "]");
                return;
            }

            // Default remote if setting upstream or upstream missing
            if (setUpstream && string.IsNullOrWhiteSpace(remote))
                remote = "origin";

            // If we DO have upstream and no explicit remote given, infer remote from upstream (origin/main -> origin)
            if (hasUpstream && string.IsNullOrWhiteSpace(remote))
            {
                var idx = upstream!.IndexOf('/');
                remote = idx > 0 ? upstream[..idx] : "origin";
            }

            // If no branch specified but upstream exists, infer branch name from upstream
            if (string.IsNullOrWhiteSpace(branch) && hasUpstream)
            {
                var idx = upstream!.IndexOf('/');
                branch = idx > 0 ? upstream[(idx + 1)..] : branch;
            }

            // 3) If upstream exists, check ahead/behind vs @{u}
            if (hasUpstream)
            {
                var (ahead, behind) = await GitAheadBehindAsync("HEAD", "@{u}", cwd, ct);
                if (behind > 0 && !allowBehind)
                {
                    Console.WriteLine($"You are behind upstream by {behind} commit(s). Pull/rebase first or use --allow-behind to attempt push.");
                    return;
                }
                if (ahead == 0 && !pushTags)
                {
                    Console.WriteLine("Nothing to push (no commits ahead). Use --tags if you want to push tags.");
                    // continue anyway in case tags set below; otherwise bail
                }
            }

            // 4) Build 'git push' args
            var pushArgs = new List<string> { "push" };
            if (dryRun) pushArgs.Add("--dry-run");
            if (forceWithLease) pushArgs.Add("--force-with-lease");
            if (setUpstream) pushArgs.Add("--set-upstream");
            if (!string.IsNullOrWhiteSpace(remote)) pushArgs.Add(remote);
            if (!string.IsNullOrWhiteSpace(branch)) pushArgs.Add(branch);
            if (pushTags) pushArgs.Add("--tags");

            // Prevent dangerous --force without lease
            if (pushArgs.Contains("--force") && !forceWithLease)
            {
                Console.WriteLine("Refusing to use '--force'. Use '--force-with-lease' for a safer forced push.");
                return;
            }

            Console.WriteLine($"Pushing: git {string.Join(' ', pushArgs)}");
            var payload = new { cmd = "git", args = pushArgs.ToArray(), cwd, timeout_ms = 600_000 };
            var resultJson = await ExecuteToolAsync("run_process", JsonSerializer.Serialize(payload), ct);

            using var doc = JsonDocument.Parse(resultJson);
            var ec = doc.RootElement.GetProperty("exit_code").GetInt32();
            var stdout = doc.RootElement.GetProperty("stdout").GetString() ?? "";
            var stderr = doc.RootElement.GetProperty("stderr").GetString() ?? "";

            if (ec == 0)
            {
                var prev = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("✅ Push successful.");
                Console.ForegroundColor = prev;
                if (!string.IsNullOrWhiteSpace(stdout)) Console.WriteLine(stdout);
            }
            else
            {
                Console.WriteLine("❌ Push failed.");
                if (!string.IsNullOrWhiteSpace(stdout)) Console.WriteLine(stdout);
                if (!string.IsNullOrWhiteSpace(stderr)) Console.Error.WriteLine(stderr);
                // Helpful hint on non-fast-forward
                if (stderr.IndexOf("non-fast-forward", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    stdout.IndexOf("non-fast-forward", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Console.WriteLine("Hint: You may need to pull/rebase first, or retry with --force-with-lease if appropriate.");
                }
            }
        }

        // --- helpers ---

        private static async Task<string?> GitRevParseAsync(string what, string abbrevFlag, string cwd, CancellationToken ct)
        {
            var payload = new { cmd = "git", args = new[] { "rev-parse", abbrevFlag, what }, cwd };
            var json = await ExecuteToolAsync("run_process", JsonSerializer.Serialize(payload), ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.GetProperty("exit_code").GetInt32() != 0) return null;
            return (doc.RootElement.GetProperty("stdout").GetString() ?? "").Trim();
        }

        private static async Task<(int ahead, int behind)> GitAheadBehindAsync(string left, string right, string cwd, CancellationToken ct)
        {
            // git rev-list --left-right --count HEAD...@{u}
            var payload = new { cmd = "git", args = new[] { "rev-list", "--left-right", "--count", $"{left}...{right}" }, cwd };
            var json = await ExecuteToolAsync("run_process", JsonSerializer.Serialize(payload), ct);
            using var doc = JsonDocument.Parse(json);
            var ec = doc.RootElement.GetProperty("exit_code").GetInt32();
            var stdout = (doc.RootElement.GetProperty("stdout").GetString() ?? "").Trim();
            if (ec != 0 || string.IsNullOrWhiteSpace(stdout)) return (0, 0);

            // Output: "<ahead>    <behind>"
            var parts = stdout.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && int.TryParse(parts[0], out var ahead) && int.TryParse(parts[1], out var behind))
                return (ahead, behind);

            return (0, 0);
        }
        private static async Task HandlePullCommandAsync(string line, CancellationToken ct)
        {
            var parts = TokenizeArgs(line);
            // parts[0] == "/pull"

            string? remote = null;
            string? branch = null;
            bool setUpstream = false;
            bool useRebase = true;        // default behavior
            bool autoStash = true;        // default behavior (git's --autostash)
            bool ffOnly = false;
            bool prune = false;
            bool dryRun = false;
            bool allowBehind = false;
            string? root = null;

            // NEW flags
            bool cleanWorkingTree = false;   // require clean tree before pulling
            bool stashUntracked = false;     // when stashing manually, include untracked
            bool noPop = false;              // keep the manual stash after pull

            for (int i = 1; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p.Equals("--remote", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Count) { remote = parts[++i]; continue; }
                if (p.Equals("--branch", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Count) { branch = parts[++i]; continue; }
                if (p.Equals("--set-upstream", StringComparison.OrdinalIgnoreCase)) { setUpstream = true; continue; }
                if (p.Equals("--merge", StringComparison.OrdinalIgnoreCase)) { useRebase = false; continue; }
                if (p.Equals("--no-autostash", StringComparison.OrdinalIgnoreCase)) { autoStash = false; continue; }
                if (p.Equals("--ff-only", StringComparison.OrdinalIgnoreCase)) { ffOnly = true; continue; }
                if (p.Equals("--prune", StringComparison.OrdinalIgnoreCase)) { prune = true; continue; }
                if (p.Equals("--dry-run", StringComparison.OrdinalIgnoreCase)) { dryRun = true; continue; }
                if (p.Equals("--allow-behind", StringComparison.OrdinalIgnoreCase)) { allowBehind = true; continue; }
                if (p.Equals("--root", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Count) { root = parts[++i]; continue; }

                // NEW
                if (p.Equals("--clean-working-tree", StringComparison.OrdinalIgnoreCase)) { cleanWorkingTree = true; continue; }
                if (p.Equals("--stash-untracked", StringComparison.OrdinalIgnoreCase)) { stashUntracked = true; continue; }
                if (p.Equals("--no-pop", StringComparison.OrdinalIgnoreCase)) { noPop = true; continue; }

                Console.WriteLine($"Unknown option: {p}");
            }

            var startDir = Directory.GetCurrentDirectory();
            var cwd = !string.IsNullOrWhiteSpace(root) ? Path.GetFullPath(root)
                    : SearchFilesToolImpl.DetectProjectRoot(startDir) ?? startDir;

            // Determine current branch if not provided
            if (string.IsNullOrWhiteSpace(branch))
            {
                branch = await GitRevParseAsync("HEAD", "--abbrev-ref", cwd, ct);
                if (string.IsNullOrWhiteSpace(branch))
                {
                    Console.WriteLine("Could not determine current branch. Are you in a git repo?");
                    return;
                }
            }

            // Check upstream (may not exist on new branches)
            var upstream = await GitRevParseAsync("@{u}", "--abbrev-ref", cwd, ct);
            bool hasUpstream = !string.IsNullOrWhiteSpace(upstream);

            // Optionally set upstream
            if (!hasUpstream && setUpstream)
            {
                if (string.IsNullOrWhiteSpace(remote)) remote = "origin";
                var tracking = $"{remote}/{branch}";
                var setArgs = new { cmd = "git", args = new[] { "branch", "--set-upstream-to", tracking, branch! }, cwd };
                var setJson = await ExecuteToolAsync("run_process", JsonSerializer.Serialize(setArgs), ct);
                using (var sdoc = JsonDocument.Parse(setJson))
                {
                    if (sdoc.RootElement.GetProperty("exit_code").GetInt32() != 0)
                    {
                        Console.WriteLine("Failed to set upstream:");
                        Console.WriteLine(sdoc.RootElement.GetProperty("stderr").GetString());
                        return;
                    }
                }
                upstream = tracking;
                hasUpstream = true;
            }

            if (!hasUpstream)
            {
                Console.WriteLine($"No upstream configured for '{branch}'.");
                Console.WriteLine("Re-run with: /pull --set-upstream [--remote origin] [--branch " + branch + "]");
                return;
            }

            // If we have upstream, and the user didn't override allow-behind, warn if behind
            if (!allowBehind)
            {
                var (ahead, behind) = await GitAheadBehindAsync("HEAD", "@{u}", cwd, ct);
                if (behind > 0)
                    Console.WriteLine($"You are behind upstream by {behind} commit(s). Proceeding with pull may cause conflicts.");
            }

            // *** NEW: Clean working tree enforcement + manual stash (incl. untracked if requested) ***
            bool didManualStash = false;
            string? manualStashName = null;

            if (cleanWorkingTree)
            {
                var status = await GetWorkingTreeStatusAsync(cwd, ct);
                if (!status.IsClean)
                {
                    if (autoStash)
                    {
                        // For safety, default to including untracked when clean mode is on,
                        // unless the user explicitly didn't ask for it (we opt-in via flag).
                        var includeUntracked = stashUntracked || true;
                        manualStashName = $"agent-autostash-{DateTime.UtcNow:yyyyMMddHHmmss}";
                        var ok = await GitStashPushAsync(cwd, includeUntracked, manualStashName, ct);
                        if (!ok)
                        {
                            Console.WriteLine("Failed to stash local changes. Aborting pull.");
                            return;
                        }
                        didManualStash = true;
                        Console.WriteLine($"Stashed working tree as '{manualStashName}'.");
                    }
                    else
                    {
                        Console.WriteLine("Working tree is dirty. Commit/stash changes or rerun with --clean-working-tree (and optional --stash-untracked).");
                        return;
                    }
                }
            }

            // Prefer a separate fetch first (more transparent output)
            {
                var fetchArgs = new List<string> { "fetch" };
                if (prune) fetchArgs.Add("--prune");
                var upstreamRemote = upstream!.Contains('/') ? upstream.Split('/')[0] : null;
                if (!string.IsNullOrWhiteSpace(upstreamRemote)) fetchArgs.Add(upstreamRemote);

                var fetchPayload = new { cmd = "git", args = fetchArgs.ToArray(), cwd, timeout_ms = 600_000 };
                var fetchJson = await ExecuteToolAsync("run_process", JsonSerializer.Serialize(fetchPayload), ct);
                using var fdoc = JsonDocument.Parse(fetchJson);
                if (fdoc.RootElement.GetProperty("exit_code").GetInt32() != 0)
                {
                    Console.WriteLine("git fetch failed:");
                    Console.WriteLine(fdoc.RootElement.GetProperty("stderr").GetString());
                    return;
                }
            }

            // Build git pull args
            var pullArgs = new List<string> { "pull" };
            if (dryRun) pullArgs.Add("--dry-run");
            if (useRebase) pullArgs.Add("--rebase"); else pullArgs.Add("--no-rebase");

            // If we already did a manual stash, leaving --autostash on is harmless (tree is now clean).
            if (autoStash) pullArgs.Add("--autostash");
            if (ffOnly) pullArgs.Add("--ff-only");
            if (prune) pullArgs.Add("--prune");

            if (!string.IsNullOrWhiteSpace(remote)) pullArgs.Add(remote);
            if (!string.IsNullOrWhiteSpace(branch)) pullArgs.Add(branch);

            Console.WriteLine($"Pulling: git {string.Join(' ', pullArgs)}");
            var pullPayload = new { cmd = "git", args = pullArgs.ToArray(), cwd, timeout_ms = 600_000 };
            var pullJson = await ExecuteToolAsync("run_process", JsonSerializer.Serialize(pullPayload), ct);

            using var pdoc = JsonDocument.Parse(pullJson);
            var ec = pdoc.RootElement.GetProperty("exit_code").GetInt32();
            var stdout = pdoc.RootElement.GetProperty("stdout").GetString() ?? "";
            var stderr = pdoc.RootElement.GetProperty("stderr").GetString() ?? "";

            if (ec == 0)
            {
                Console.WriteLine("✅ Pull completed.");
                if (!string.IsNullOrWhiteSpace(stdout)) Console.WriteLine(stdout);

                // Auto-pop manual stash if we made one
                if (didManualStash && !noPop)
                {
                    Console.WriteLine("Restoring stashed changes...");
                    var popped = await GitStashPopAsync(cwd, ct);
                    if (!popped.ok)
                    {
                        Console.WriteLine("Stash pop reported issues:");
                        if (!string.IsNullOrWhiteSpace(popped.stdout)) Console.WriteLine(popped.stdout);
                        if (!string.IsNullOrWhiteSpace(popped.stderr)) Console.Error.WriteLine(popped.stderr);
                        Console.WriteLine("Resolve conflicts, then continue as needed (e.g., /run git add ..., /run git rebase --continue).");
                    }
                }

                var (ahead2, behind2) = await GitAheadBehindAsync("HEAD", "@{u}", cwd, ct);
                Console.WriteLine($"Status vs upstream: ahead {ahead2}, behind {behind2}");
                return;
            }

            Console.WriteLine("❌ Pull failed.");
            if (!string.IsNullOrWhiteSpace(stdout)) Console.WriteLine(stdout);
            if (!string.IsNullOrWhiteSpace(stderr)) Console.Error.WriteLine(stderr);

            // Conflict hints
            if (useRebase)
            {
                if (stdout.IndexOf("CONFLICT", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    stderr.IndexOf("CONFLICT", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    stdout.IndexOf("rebase in progress", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    stderr.IndexOf("rebase in progress", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Console.WriteLine("Resolve conflicts, then run:");
                    Console.WriteLine("  /run git add <files>");
                    Console.WriteLine("  /run git rebase --continue");
                    Console.WriteLine("Or abort with:");
                    Console.WriteLine("  /run git rebase --abort");
                    return;
                }
            }
            else
            {
                if (stdout.IndexOf("CONFLICT", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    stderr.IndexOf("CONFLICT", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Console.WriteLine("Resolve conflicts, then run:");
                    Console.WriteLine("  /run git add <files>");
                    Console.WriteLine("  /run git commit   (or `git merge --continue` on newer git)");
                    Console.WriteLine("Or abort with:");
                    Console.WriteLine("  /run git merge --abort");
                    return;
                }
            }

            Console.WriteLine("Hint: check repository state with `/run git status`.");
        }
        // Quick working tree summary
        private sealed class WorkingTreeStatus
        {
            public bool IsClean { get; init; }
            public bool HasUntracked { get; init; }
            public bool HasUnmerged { get; init; }
            public bool HasStagedOrModified { get; init; }
        }

        private static async Task<WorkingTreeStatus> GetWorkingTreeStatusAsync(string cwd, CancellationToken ct)
        {
            // --porcelain shows simple 2-letter status codes
            var payload = new { cmd = "git", args = new[] { "status", "--porcelain" }, cwd };
            var json = await ExecuteToolAsync("run_process", JsonSerializer.Serialize(payload), ct);
            using var doc = JsonDocument.Parse(json);
            var stdout = (doc.RootElement.GetProperty("stdout").GetString() ?? "").Replace("\r\n", "\n");
            var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            bool hasUntracked = false, hasUnmerged = false, hasStagedOrModified = false;
            foreach (var l in lines)
            {
                // Lines like: "?? file", " M file", "M  file", "UU file"
                if (l.StartsWith("?? ")) hasUntracked = true;
                if (l.StartsWith("UU ") || l.StartsWith("AA ") || l.StartsWith("DD ")) hasUnmerged = true;
                if (!l.StartsWith("?? ")) hasStagedOrModified = true; // anything tracked and changed
            }

            return new WorkingTreeStatus
            {
                IsClean = lines.Length == 0,
                HasUntracked = hasUntracked,
                HasUnmerged = hasUnmerged,
                HasStagedOrModified = hasStagedOrModified
            };
        }

        // Manual stash (optionally includes untracked)
        private static async Task<bool> GitStashPushAsync(string cwd, bool includeUntracked, string? message, CancellationToken ct)
        {
            var args = new List<string> { "stash", "push" };
            if (!string.IsNullOrWhiteSpace(message)) { args.Add("-m"); args.Add(message!); }
            if (includeUntracked) args.Add("--include-untracked");

            var payload = new { cmd = "git", args = args.ToArray(), cwd };
            var json = await ExecuteToolAsync("run_process", JsonSerializer.Serialize(payload), ct);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("exit_code").GetInt32() == 0;
        }

        private static async Task<(bool ok, string stdout, string stderr)> GitStashPopAsync(string cwd, CancellationToken ct)
        {
            var payload = new { cmd = "git", args = new[] { "stash", "pop", "--index" }, cwd };
            var json = await ExecuteToolAsync("run_process", JsonSerializer.Serialize(payload), ct);
            using var doc = JsonDocument.Parse(json);
            var ok = doc.RootElement.GetProperty("exit_code").GetInt32() == 0;
            var stdout = doc.RootElement.GetProperty("stdout").GetString() ?? "";
            var stderr = doc.RootElement.GetProperty("stderr").GetString() ?? "";
            return (ok, stdout, stderr);
        }

        private static async Task<int?> GetContextLengthAsync(HttpClient http, string modelId, CancellationToken ct)
        {
            using var resp = await http.GetAsync($"/api/v0/models/{Uri.EscapeDataString(modelId)}", ct);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;

            if (root.TryGetProperty("max_context_length", out var m) && m.ValueKind == JsonValueKind.Number)
                return m.GetInt32();

            // Fallback if the API shape changes:
            if (root.TryGetProperty("model_info", out var mi) &&
                mi.ValueKind == JsonValueKind.Object &&
                mi.TryGetProperty("context_length", out var cl) &&
                cl.ValueKind == JsonValueKind.Number)
                return cl.GetInt32();

            return null;
        }
        // /config show|path|reload|save
        private static Task HandleConfigCommandAsync(string line, HttpClient http, CancellationToken ct)
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
            var args = TokenizeArgs(line); // you already have this helper
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
                        AgentConfig.Config.HostUrl = uri.ToString().TrimEnd('/'); // normalize (optional)
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
        private static void PrintHelp()
        {
            Console.WriteLine("(T)ool for (H)eurustic (U)niversal (V)ersatile (U)sage (THUVU)");
            Console.WriteLine("Commands:");
            Console.WriteLine("  /help                         Show this help");
            Console.WriteLine("  /exit                         Quit");
            Console.WriteLine("  /clear                        Reset conversation (keeps current system prompt)");
            Console.WriteLine("  /system <text>                Set system prompt");
            Console.WriteLine("  /stream on|off                Toggle token-by-token streaming");
            Console.WriteLine("  /diff [--staged] [--context N] [--root PATH] [PATH ...]");
            Console.WriteLine("       Show a git unified diff. Example: /diff --staged --context 5 src/");
            Console.WriteLine("  /test [SOLUTION_OR_PROJECT] [--filter EXP] [--logger trx|console]");
            Console.WriteLine("       Run dotnet tests and print a summary. Example: /test tests/MyTests.csproj --filter \"FullyQualifiedName~MySuite\"");
            Console.WriteLine("  /run CMD [ARGS ...] [--cwd PATH] [--timeout MS]");
            Console.WriteLine("       Run a whitelisted command (dotnet, git, bash, powershell). Example:");
            Console.WriteLine("       /run dotnet build MyApp.sln -c Release");
            Console.WriteLine("  /commit \"message\" [--all] [--staged] [--no-test] [--allow-empty] [--root PATH]");
            Console.WriteLine("       Gate on green tests (unless --no-test). --all stages all changes; --staged uses staged only.");
            Console.WriteLine("       Example: /commit \"Fix HttpClient wrapper\" --all");
            Console.WriteLine("  /push [--remote NAME] [--branch NAME] [--set-upstream] [--force-with-lease] [--tags] [--dry-run] [--allow-behind] [--root PATH]");
            Console.WriteLine("       Safe push: confirms branch/upstream and blocks non-fast-forward unless --force-with-lease.");
            Console.WriteLine("       Examples:");
            Console.WriteLine("         /push");
            Console.WriteLine("         /push --set-upstream --remote origin");
            Console.WriteLine("         /push --branch feature/foo --force-with-lease");
            Console.WriteLine("  /pull [--remote NAME] [--branch NAME] [--set-upstream] [--merge] [--no-autostash] [--ff-only] [--prune]");
            Console.WriteLine("        [--dry-run] [--allow-behind] [--clean-working-tree] [--stash-untracked] [--no-pop] [--root PATH]");
            Console.WriteLine("       Safe pull: defaults to --rebase with autostash. With --clean-working-tree,");
            Console.WriteLine("       the command will stash (including untracked unless you omit --stash-untracked) or abort if dirty.");
            Console.WriteLine("       Examples:");
            Console.WriteLine("         /pull");
            Console.WriteLine("         /pull --clean-working-tree --stash-untracked");
            Console.WriteLine("         /pull --merge --ff-only");
            Console.WriteLine("  /config[show | path | reload | save]   Inspect or manage config file");
            Console.WriteLine("  /set model<id> Change model id and persist");
            Console.WriteLine("  /set host<url> Change LM Studio host URL and persist");
            Console.WriteLine("  /set stream on| off                Toggle streaming and persist");
            Console.WriteLine("  /set timeout<ms> Default timeout for / run & dotnet / git tools");
            Console.WriteLine("  /test-permissions                Test permission system functionality");
            Console.WriteLine();
            Console.WriteLine("Permission System:");
            Console.WriteLine("  Read-only tools (search_files, read_file, git_status, git_diff, nuget_search) are always allowed.");
            Console.WriteLine("  Write tools require user permission. You'll be prompted to allow:");
            Console.WriteLine("    [A] Always for this repo (persistent)");
            Console.WriteLine("    [S] For this session (temporary)");
            Console.WriteLine("    [O] Once (this time only)");
            Console.WriteLine("    [N] No (cancel operation)");

        }



    }


}
