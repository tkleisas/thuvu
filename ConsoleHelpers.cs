using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using CodingAgent;
using thuvu.Models;

namespace thuvu
{
    /// <summary>
    /// Console output utilities for formatting and display
    /// </summary>
    public static class ConsoleHelpers
    {
        /// <summary>
        /// Print a unified diff with syntax highlighting
        /// </summary>
        public static void PrintUnifiedDiff(string patch)
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
        }

        /// <summary>
        /// Execute an action with a specific console color
        /// </summary>
        public static void WithColor(ConsoleColor color, Action act)
        {
            var prev = Console.ForegroundColor;
            try { Console.ForegroundColor = color; act(); }
            finally { Console.ForegroundColor = prev; }
        }

        /// <summary>
        /// Auto pretty-print tool results based on content type
        /// </summary>
        public static void AutoPrettyPrinterCallback(string toolName, string toolResultJson)
        {
            try
            {
                using var doc = JsonDocument.Parse(toolResultJson);
                var root = doc.RootElement;

                // Look for stdout-bearing tools (run_process, git_*, dotnet_*)
                if (root.TryGetProperty("stdout", out var stdoutEl) && stdoutEl.ValueKind == JsonValueKind.String)
                {
                    var stdout = stdoutEl.GetString() ?? "";

                    // Diffs: git_diff or anything that looks like a unified diff
                    if (toolName == "git_diff" || LooksLikeUnifiedDiff(stdout))
                    {
                        Console.WriteLine();
                        PrintUnifiedDiff(stdout);
                        Console.WriteLine();
                        return;
                    }

                    // Tests: dotnet_test or output that looks like a test summary
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

        /// <summary>
        /// Check if text looks like a unified diff
        /// </summary>
        public static bool LooksLikeUnifiedDiff(string text)
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

        /// <summary>
        /// Split a command line into tokens, respecting quotes
        /// </summary>
        public static List<string> TokenizeArgs(string text)
        {
            var res = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return res;

            var sb = new System.Text.StringBuilder();
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

        /// <summary>
        /// Print help text for all commands
        /// </summary>
        public static void PrintHelp()
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
            Console.WriteLine("RAG (Retrieval-Augmented Generation):");
            Console.WriteLine("  /rag config                    Show RAG configuration");
            Console.WriteLine("  /rag enable                    Enable RAG (requires PostgreSQL with pgvector)");
            Console.WriteLine("  /rag disable                   Disable RAG");
            Console.WriteLine("  /rag stats                     Show RAG index statistics");
            Console.WriteLine("  /rag index PATH [--recursive] [--pattern GLOB]");
            Console.WriteLine("       Index files for semantic search. Example: /rag index src/ --recursive --pattern *.cs");
            Console.WriteLine("  /rag search QUERY [--top N]    Search indexed content semantically");
            Console.WriteLine("  /rag clear [PATH]              Clear RAG index (all or specific source)");
            Console.WriteLine();
            Console.WriteLine("Permission System:");
            Console.WriteLine("  Read-only tools (search_files, read_file, git_status, git_diff, nuget_search, rag_search, rag_stats) are always allowed.");
            Console.WriteLine("  Write tools require user permission. You'll be prompted to allow:");
            Console.WriteLine("    [A] Always for this repo (persistent)");
            Console.WriteLine("    [S] For this session (temporary)");
            Console.WriteLine("    [O] Once (this time only)");
            Console.WriteLine("    [N] No (cancel operation)");
            Console.WriteLine();
            Console.WriteLine("MCP (Model Context Protocol) Code Execution:");
            Console.WriteLine("  /mcp config                    Show MCP configuration");
            Console.WriteLine("  /mcp enable                    Enable MCP code execution (requires Deno)");
            Console.WriteLine("  /mcp disable                   Disable MCP code execution");
            Console.WriteLine("  /mcp on                        Activate MCP mode (agent writes TypeScript)");
            Console.WriteLine("  /mcp off                       Deactivate MCP mode (traditional tool calling)");
            Console.WriteLine("  /mcp check                     Check MCP environment (Deno, directories)");
            Console.WriteLine("  /mcp status                    Show current MCP status");
            Console.WriteLine("  /mcp tools                     List available MCP tools");
            Console.WriteLine("  /mcp run \"<code>\"              Execute TypeScript code in sandbox");
            Console.WriteLine("       Example: /mcp run \"const files = await searchFiles('**/*.cs'); return files.length;\"");
            Console.WriteLine();
            Console.WriteLine("Skills (Saved TypeScript Workflows):");
            Console.WriteLine("  /mcp skill list                List all saved skills");
            Console.WriteLine("  /mcp skill run <name> [params] Run a saved skill");
            Console.WriteLine("  /mcp skill save <name> \"code\"  Save a new skill");
            Console.WriteLine("  /mcp skill delete <name>       Delete a skill");
            Console.WriteLine();
            Console.WriteLine("Security:");
            Console.WriteLine("  /mcp permissions               Show/set permission level");
            Console.WriteLine("  /mcp permissions set <level>   Set level (readonly|readwrite|execute|full)");
            Console.WriteLine("  /mcp permissions approval on|off  Toggle code execution approval");
            Console.WriteLine("  /mcp audit on|off              Toggle audit logging");
        }
    }
}
