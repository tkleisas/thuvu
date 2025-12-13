using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using thuvu.Models;
using thuvu.Tools;

namespace thuvu
{
    /// <summary>
    /// Handlers for git-related commands (/push, /pull) and helper methods
    /// </summary>
    public static class GitCommandHandlers
    {
        // /push [--remote NAME] [--branch NAME] [--set-upstream] [--force-with-lease] [--tags] [--dry-run] [--allow-behind] [--root PATH]
        public static async Task HandlePushCommandAsync(string line, CancellationToken ct)
        {
            var parts = ConsoleHelpers.TokenizeArgs(line);

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

            // Check upstream
            var upstream = await GitRevParseAsync("@{u}", "--abbrev-ref", cwd, ct);
            bool hasUpstream = !string.IsNullOrWhiteSpace(upstream);

            if (!hasUpstream && !setUpstream)
            {
                Console.WriteLine($"No upstream configured for '{branch}'.");
                Console.WriteLine("Re-run with: /push --set-upstream [--remote origin] [--branch " + branch + "]");
                return;
            }

            if (setUpstream && string.IsNullOrWhiteSpace(remote))
                remote = "origin";

            if (hasUpstream && string.IsNullOrWhiteSpace(remote))
            {
                var idx = upstream!.IndexOf('/');
                remote = idx > 0 ? upstream[..idx] : "origin";
            }

            if (string.IsNullOrWhiteSpace(branch) && hasUpstream)
            {
                var idx = upstream!.IndexOf('/');
                branch = idx > 0 ? upstream[(idx + 1)..] : branch;
            }

            // Check ahead/behind
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
                }
            }

            // Build push args
            var pushArgs = new List<string> { "push" };
            if (dryRun) pushArgs.Add("--dry-run");
            if (forceWithLease) pushArgs.Add("--force-with-lease");
            if (setUpstream) pushArgs.Add("--set-upstream");
            if (!string.IsNullOrWhiteSpace(remote)) pushArgs.Add(remote);
            if (!string.IsNullOrWhiteSpace(branch)) pushArgs.Add(branch);
            if (pushTags) pushArgs.Add("--tags");

            if (pushArgs.Contains("--force") && !forceWithLease)
            {
                Console.WriteLine("Refusing to use '--force'. Use '--force-with-lease' for a safer forced push.");
                return;
            }

            Console.WriteLine($"Pushing: git {string.Join(' ', pushArgs)}");
            var payload = new { cmd = "git", args = pushArgs.ToArray(), cwd, timeout_ms = 600_000 };
            var resultJson = await ToolExecutor.ExecuteToolAsync("run_process", JsonSerializer.Serialize(payload), ct);

            using var doc = JsonDocument.Parse(resultJson);
            var ec = doc.RootElement.GetProperty("exit_code").GetInt32();
            var stdout = doc.RootElement.GetProperty("stdout").GetString() ?? "";
            var stderr = doc.RootElement.GetProperty("stderr").GetString() ?? "";

            if (ec == 0)
            {
                ConsoleHelpers.WithColor(ConsoleColor.Green, () => Console.WriteLine("✅ Push successful."));
                if (!string.IsNullOrWhiteSpace(stdout)) Console.WriteLine(stdout);
            }
            else
            {
                Console.WriteLine("❌ Push failed.");
                if (!string.IsNullOrWhiteSpace(stdout)) Console.WriteLine(stdout);
                if (!string.IsNullOrWhiteSpace(stderr)) Console.Error.WriteLine(stderr);
                if (stderr.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase) ||
                    stdout.Contains("non-fast-forward", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("Hint: You may need to pull/rebase first, or retry with --force-with-lease if appropriate.");
                }
            }
        }

        // /pull [--remote NAME] [--branch NAME] [--set-upstream] [--merge] [--no-autostash] [--ff-only] [--prune]
        //       [--dry-run] [--allow-behind] [--clean-working-tree] [--stash-untracked] [--no-pop] [--root PATH]
        public static async Task HandlePullCommandAsync(string line, CancellationToken ct)
        {
            var parts = ConsoleHelpers.TokenizeArgs(line);

            string? remote = null;
            string? branch = null;
            bool setUpstream = false;
            bool useRebase = true;
            bool autoStash = true;
            bool ffOnly = false;
            bool prune = false;
            bool dryRun = false;
            bool allowBehind = false;
            string? root = null;
            bool cleanWorkingTree = false;
            bool stashUntracked = false;
            bool noPop = false;

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
                if (p.Equals("--clean-working-tree", StringComparison.OrdinalIgnoreCase)) { cleanWorkingTree = true; continue; }
                if (p.Equals("--stash-untracked", StringComparison.OrdinalIgnoreCase)) { stashUntracked = true; continue; }
                if (p.Equals("--no-pop", StringComparison.OrdinalIgnoreCase)) { noPop = true; continue; }
                Console.WriteLine($"Unknown option: {p}");
            }

            var startDir = Directory.GetCurrentDirectory();
            var cwd = !string.IsNullOrWhiteSpace(root) ? Path.GetFullPath(root)
                    : SearchFilesToolImpl.DetectProjectRoot(startDir) ?? startDir;

            // Determine branch
            if (string.IsNullOrWhiteSpace(branch))
            {
                branch = await GitRevParseAsync("HEAD", "--abbrev-ref", cwd, ct);
                if (string.IsNullOrWhiteSpace(branch))
                {
                    Console.WriteLine("Could not determine current branch. Are you in a git repo?");
                    return;
                }
            }

            // Check upstream
            var upstream = await GitRevParseAsync("@{u}", "--abbrev-ref", cwd, ct);
            bool hasUpstream = !string.IsNullOrWhiteSpace(upstream);

            if (!hasUpstream && setUpstream)
            {
                if (string.IsNullOrWhiteSpace(remote)) remote = "origin";
                var tracking = $"{remote}/{branch}";
                var setArgs = new { cmd = "git", args = new[] { "branch", "--set-upstream-to", tracking, branch! }, cwd };
                var setJson = await ToolExecutor.ExecuteToolAsync("run_process", JsonSerializer.Serialize(setArgs), ct);
                using var sdoc = JsonDocument.Parse(setJson);
                if (sdoc.RootElement.GetProperty("exit_code").GetInt32() != 0)
                {
                    Console.WriteLine("Failed to set upstream:");
                    Console.WriteLine(sdoc.RootElement.GetProperty("stderr").GetString());
                    return;
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

            if (!allowBehind)
            {
                var (ahead, behind) = await GitAheadBehindAsync("HEAD", "@{u}", cwd, ct);
                if (behind > 0)
                    Console.WriteLine($"You are behind upstream by {behind} commit(s). Proceeding with pull may cause conflicts.");
            }

            // Handle stashing
            bool didManualStash = false;
            string? manualStashName = null;

            if (cleanWorkingTree)
            {
                var status = await GetWorkingTreeStatusAsync(cwd, ct);
                if (!status.IsClean)
                {
                    if (autoStash)
                    {
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
                        Console.WriteLine("Working tree is dirty. Commit/stash changes or rerun with --clean-working-tree.");
                        return;
                    }
                }
            }

            // Fetch first
            {
                var fetchArgs = new List<string> { "fetch" };
                if (prune) fetchArgs.Add("--prune");
                var upstreamRemote = upstream!.Contains('/') ? upstream.Split('/')[0] : null;
                if (!string.IsNullOrWhiteSpace(upstreamRemote)) fetchArgs.Add(upstreamRemote);

                var fetchPayload = new { cmd = "git", args = fetchArgs.ToArray(), cwd, timeout_ms = 600_000 };
                var fetchJson = await ToolExecutor.ExecuteToolAsync("run_process", JsonSerializer.Serialize(fetchPayload), ct);
                using var fdoc = JsonDocument.Parse(fetchJson);
                if (fdoc.RootElement.GetProperty("exit_code").GetInt32() != 0)
                {
                    Console.WriteLine("git fetch failed:");
                    Console.WriteLine(fdoc.RootElement.GetProperty("stderr").GetString());
                    return;
                }
            }

            // Build pull args
            var pullArgs = new List<string> { "pull" };
            if (dryRun) pullArgs.Add("--dry-run");
            if (useRebase) pullArgs.Add("--rebase"); else pullArgs.Add("--no-rebase");
            if (autoStash) pullArgs.Add("--autostash");
            if (ffOnly) pullArgs.Add("--ff-only");
            if (prune) pullArgs.Add("--prune");
            if (!string.IsNullOrWhiteSpace(remote)) pullArgs.Add(remote);
            if (!string.IsNullOrWhiteSpace(branch)) pullArgs.Add(branch);

            Console.WriteLine($"Pulling: git {string.Join(' ', pullArgs)}");
            var pullPayload = new { cmd = "git", args = pullArgs.ToArray(), cwd, timeout_ms = 600_000 };
            var pullJson = await ToolExecutor.ExecuteToolAsync("run_process", JsonSerializer.Serialize(pullPayload), ct);

            using var pdoc = JsonDocument.Parse(pullJson);
            var ec = pdoc.RootElement.GetProperty("exit_code").GetInt32();
            var stdout = pdoc.RootElement.GetProperty("stdout").GetString() ?? "";
            var stderr = pdoc.RootElement.GetProperty("stderr").GetString() ?? "";

            if (ec == 0)
            {
                Console.WriteLine("✅ Pull completed.");
                if (!string.IsNullOrWhiteSpace(stdout)) Console.WriteLine(stdout);

                if (didManualStash && !noPop)
                {
                    Console.WriteLine("Restoring stashed changes...");
                    var popped = await GitStashPopAsync(cwd, ct);
                    if (!popped.ok)
                    {
                        Console.WriteLine("Stash pop reported issues:");
                        if (!string.IsNullOrWhiteSpace(popped.stdout)) Console.WriteLine(popped.stdout);
                        if (!string.IsNullOrWhiteSpace(popped.stderr)) Console.Error.WriteLine(popped.stderr);
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
            if (useRebase && (stdout.Contains("CONFLICT", StringComparison.OrdinalIgnoreCase) ||
                stderr.Contains("CONFLICT", StringComparison.OrdinalIgnoreCase)))
            {
                Console.WriteLine("Resolve conflicts, then run:");
                Console.WriteLine("  /run git add <files>");
                Console.WriteLine("  /run git rebase --continue");
                Console.WriteLine("Or abort with:");
                Console.WriteLine("  /run git rebase --abort");
            }
        }

        // Git helper methods
        public static async Task<string?> GitRevParseAsync(string what, string abbrevFlag, string cwd, CancellationToken ct)
        {
            var payload = new { cmd = "git", args = new[] { "rev-parse", abbrevFlag, what }, cwd };
            var json = await ToolExecutor.ExecuteToolAsync("run_process", JsonSerializer.Serialize(payload), ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.GetProperty("exit_code").GetInt32() != 0) return null;
            return (doc.RootElement.GetProperty("stdout").GetString() ?? "").Trim();
        }

        public static async Task<(int ahead, int behind)> GitAheadBehindAsync(string left, string right, string cwd, CancellationToken ct)
        {
            var payload = new { cmd = "git", args = new[] { "rev-list", "--left-right", "--count", $"{left}...{right}" }, cwd };
            var json = await ToolExecutor.ExecuteToolAsync("run_process", JsonSerializer.Serialize(payload), ct);
            using var doc = JsonDocument.Parse(json);
            var ec = doc.RootElement.GetProperty("exit_code").GetInt32();
            var stdout = (doc.RootElement.GetProperty("stdout").GetString() ?? "").Trim();
            if (ec != 0 || string.IsNullOrWhiteSpace(stdout)) return (0, 0);

            var parts = stdout.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && int.TryParse(parts[0], out var ahead) && int.TryParse(parts[1], out var behind))
                return (ahead, behind);

            return (0, 0);
        }

        private sealed class WorkingTreeStatus
        {
            public bool IsClean { get; init; }
            public bool HasUntracked { get; init; }
            public bool HasUnmerged { get; init; }
            public bool HasStagedOrModified { get; init; }
        }

        private static async Task<WorkingTreeStatus> GetWorkingTreeStatusAsync(string cwd, CancellationToken ct)
        {
            var payload = new { cmd = "git", args = new[] { "status", "--porcelain" }, cwd };
            var json = await ToolExecutor.ExecuteToolAsync("run_process", JsonSerializer.Serialize(payload), ct);
            using var doc = JsonDocument.Parse(json);
            var stdout = (doc.RootElement.GetProperty("stdout").GetString() ?? "").Replace("\r\n", "\n");
            var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            bool hasUntracked = false, hasUnmerged = false, hasStagedOrModified = false;
            foreach (var l in lines)
            {
                if (l.StartsWith("?? ")) hasUntracked = true;
                if (l.StartsWith("UU ") || l.StartsWith("AA ") || l.StartsWith("DD ")) hasUnmerged = true;
                if (!l.StartsWith("?? ")) hasStagedOrModified = true;
            }

            return new WorkingTreeStatus
            {
                IsClean = lines.Length == 0,
                HasUntracked = hasUntracked,
                HasUnmerged = hasUnmerged,
                HasStagedOrModified = hasStagedOrModified
            };
        }

        private static async Task<bool> GitStashPushAsync(string cwd, bool includeUntracked, string? message, CancellationToken ct)
        {
            var args = new List<string> { "stash", "push" };
            if (!string.IsNullOrWhiteSpace(message)) { args.Add("-m"); args.Add(message!); }
            if (includeUntracked) args.Add("--include-untracked");

            var payload = new { cmd = "git", args = args.ToArray(), cwd };
            var json = await ToolExecutor.ExecuteToolAsync("run_process", JsonSerializer.Serialize(payload), ct);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("exit_code").GetInt32() == 0;
        }

        private static async Task<(bool ok, string stdout, string stderr)> GitStashPopAsync(string cwd, CancellationToken ct)
        {
            var payload = new { cmd = "git", args = new[] { "stash", "pop", "--index" }, cwd };
            var json = await ToolExecutor.ExecuteToolAsync("run_process", JsonSerializer.Serialize(payload), ct);
            using var doc = JsonDocument.Parse(json);
            var ok = doc.RootElement.GetProperty("exit_code").GetInt32() == 0;
            var stdout = doc.RootElement.GetProperty("stdout").GetString() ?? "";
            var stderr = doc.RootElement.GetProperty("stderr").GetString() ?? "";
            return (ok, stdout, stderr);
        }
    }
}
