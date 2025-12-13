using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using thuvu.Models;

namespace thuvu
{
    /// <summary>
    /// Handlers for MCP-related commands (/mcp)
    /// </summary>
    public static class McpCommandHandlers
    {
        // /mcp config|enable|disable|on|off|check|run|tools|status
        public static async Task HandleMcpCommandAsync(string line, CancellationToken ct)
        {
            var parts = ConsoleHelpers.TokenizeArgs(line);
            if (parts.Count < 2)
            {
                Console.WriteLine("Usage: /mcp config|enable|disable|on|off|check|run|tools|status");
                return;
            }

            var subCommand = parts[1].ToLowerInvariant();

            switch (subCommand)
            {
                case "config":
                    Console.WriteLine($"MCP Configuration:");
                    Console.WriteLine($"  Enabled: {McpConfig.Instance.Enabled}");
                    Console.WriteLine($"  Deno Path: {McpConfig.Instance.DenoPath}");
                    Console.WriteLine($"  Default Timeout: {McpConfig.Instance.DefaultTimeout}ms");
                    Console.WriteLine($"  Max Memory: {McpConfig.Instance.MaxMemoryMb}MB");
                    Console.WriteLine($"  Permission Level: {McpConfig.Instance.PermissionLevel}");
                    Console.WriteLine($"  Skills Directory: {McpConfig.Instance.SkillsDirectory}");
                    Console.WriteLine($"  Audit Log: {McpConfig.Instance.AuditLog}");
                    Console.WriteLine($"  Require Approval: {McpConfig.Instance.RequireApproval}");
                    Console.WriteLine($"  Config path: {McpConfig.GetConfigPath()}");
                    break;

                case "enable":
                    McpConfig.Instance.Enabled = true;
                    McpConfig.SaveConfig();
                    Console.WriteLine("MCP code execution enabled.");

                    if (!await McpCodeExecutor.IsDenoAvailableAsync())
                    {
                        ConsoleHelpers.WithColor(ConsoleColor.Yellow, () =>
                            Console.WriteLine("Warning: Deno runtime not found. Please install Deno: https://deno.land"));
                    }
                    break;

                case "disable":
                    McpConfig.Instance.Enabled = false;
                    McpConfig.SaveConfig();
                    Console.WriteLine("MCP code execution disabled.");
                    break;

                case "on":
                    if (!McpConfig.Instance.Enabled)
                    {
                        Console.WriteLine("MCP is not enabled. Run '/mcp enable' first.");
                        return;
                    }
                    if (!await McpCodeExecutor.IsDenoAvailableAsync())
                    {
                        Console.WriteLine("Deno runtime not found. Please install Deno: https://deno.land");
                        return;
                    }
                    McpConfig.Instance.McpModeActive = true;
                    ConsoleHelpers.WithColor(ConsoleColor.Green, () => Console.WriteLine("✓ MCP code execution mode activated."));
                    Console.WriteLine("The agent will now write TypeScript code to execute tools.");
                    Console.WriteLine("Use '/mcp off' to switch back to traditional tool calling.");
                    break;

                case "off":
                    McpConfig.Instance.McpModeActive = false;
                    Console.WriteLine("MCP code execution mode deactivated. Using traditional tool calling.");
                    break;

                case "check":
                    Console.WriteLine("Checking MCP environment...");

                    var denoAvailable = await McpCodeExecutor.IsDenoAvailableAsync();
                    Console.Write("  Deno runtime: ");
                    if (denoAvailable)
                    {
                        ConsoleHelpers.WithColor(ConsoleColor.Green, () => Console.WriteLine("✓ Available"));
                    }
                    else
                    {
                        ConsoleHelpers.WithColor(ConsoleColor.Red, () => Console.WriteLine("✗ Not found"));
                    }

                    var mcpPath = Path.Combine(Directory.GetCurrentDirectory(), "mcp");
                    Console.Write("  MCP directory: ");
                    if (Directory.Exists(mcpPath))
                    {
                        ConsoleHelpers.WithColor(ConsoleColor.Green, () => Console.WriteLine($"✓ Found at {mcpPath}"));
                    }
                    else
                    {
                        ConsoleHelpers.WithColor(ConsoleColor.Red, () => Console.WriteLine($"✗ Not found at {mcpPath}"));
                    }

                    var sandboxPath = Path.Combine(mcpPath, "runtime", "sandbox.ts");
                    Console.Write("  Sandbox script: ");
                    if (File.Exists(sandboxPath))
                    {
                        ConsoleHelpers.WithColor(ConsoleColor.Green, () => Console.WriteLine("✓ Found"));
                    }
                    else
                    {
                        ConsoleHelpers.WithColor(ConsoleColor.Red, () => Console.WriteLine("✗ Not found"));
                    }

                    Console.WriteLine();
                    Console.WriteLine($"MCP is {(McpConfig.Instance.Enabled ? "ENABLED" : "DISABLED")}");
                    break;

                case "tools":
                    Console.WriteLine("Available MCP tools:");
                    var bridge = new McpBridge();
                    foreach (var tool in bridge.GetAvailableTools())
                    {
                        Console.WriteLine($"  - {tool}");
                    }
                    break;

                case "run":
                    if (parts.Count < 3)
                    {
                        Console.WriteLine("Usage: /mcp run \"<typescript code>\"");
                        Console.WriteLine("Example: /mcp run \"const files = await searchFiles('**/*.cs'); return files.length;\"");
                        return;
                    }

                    if (!McpConfig.Instance.Enabled)
                    {
                        Console.WriteLine("MCP is disabled. Enable with: /mcp enable");
                        return;
                    }

                    var code = string.Join(" ", parts.Skip(2)).Trim('"');

                    Console.WriteLine("Executing TypeScript code...");
                    Console.WriteLine();

                    using (var executor = new McpCodeExecutor())
                    {
                        var result = await executor.ExecuteAsync(code, ct);

                        if (result.Success)
                        {
                            ConsoleHelpers.WithColor(ConsoleColor.Green, () => Console.WriteLine("✓ Execution successful"));

                            if (!string.IsNullOrEmpty(result.Result))
                            {
                                Console.WriteLine("Result:");
                                Console.WriteLine(result.Result);
                            }
                        }
                        else
                        {
                            ConsoleHelpers.WithColor(ConsoleColor.Red, () => Console.WriteLine("✗ Execution failed"));

                            if (!string.IsNullOrEmpty(result.Error))
                            {
                                Console.WriteLine($"Error: {result.Error}");
                            }
                        }

                        Console.WriteLine();
                        Console.WriteLine($"Duration: {result.Duration.TotalMilliseconds:F2}ms");
                        Console.WriteLine($"Tool calls: {result.ToolCalls.Count}");

                        if (result.ToolCalls.Count > 0)
                        {
                            Console.WriteLine();
                            Console.WriteLine("Tool call log:");
                            foreach (var call in result.ToolCalls)
                            {
                                var status = call.Success ? "✓" : "✗";
                                Console.WriteLine($"  {status} {call.ToolName} ({call.Duration.TotalMilliseconds:F2}ms)");
                            }
                        }
                    }
                    break;

                case "status":
                    Console.WriteLine("MCP Status:");
                    Console.WriteLine($"  Enabled: {McpConfig.Instance.Enabled}");
                    Console.WriteLine($"  Mode Active: {McpConfig.Instance.McpModeActive}");
                    Console.WriteLine($"  Deno Available: {(await McpCodeExecutor.IsDenoAvailableAsync() ? "Yes" : "No")}");
                    break;

                case "skill":
                    await HandleSkillSubcommandAsync(parts, ct);
                    break;

                case "permissions":
                    HandlePermissionsCommand(parts);
                    break;

                case "audit":
                    HandleAuditCommand(parts);
                    break;

                default:
                    Console.WriteLine("Unknown MCP command. Available: config, enable, disable, on, off, check, run, tools, status, skill, permissions, audit");
                    break;
            }
        }

        /// <summary>
        /// Handle skill subcommands: list, run, save, delete
        /// </summary>
        private static async Task HandleSkillSubcommandAsync(List<string> parts, CancellationToken ct)
        {
            if (parts.Count < 3)
            {
                Console.WriteLine("Usage: /mcp skill list|run|save|delete [args]");
                Console.WriteLine("  /mcp skill list                  - List all saved skills");
                Console.WriteLine("  /mcp skill run <name> [params]   - Run a skill");
                Console.WriteLine("  /mcp skill save <name> \"code\"    - Save a skill");
                Console.WriteLine("  /mcp skill delete <name>         - Delete a skill");
                return;
            }

            var action = parts[2].ToLowerInvariant();

            switch (action)
            {
                case "list":
                    var skills = SkillManager.ListSkills();
                    if (skills.Count == 0)
                    {
                        Console.WriteLine("No skills saved yet. Use '/mcp skill save <name> \"code\"' to create one.");
                        return;
                    }

                    Console.WriteLine($"Available skills ({skills.Count}):");
                    Console.WriteLine();
                    foreach (var skill in skills)
                    {
                        ConsoleHelpers.WithColor(ConsoleColor.Cyan, () => Console.WriteLine($"  {skill.Name}"));
                        if (!string.IsNullOrEmpty(skill.Description))
                        {
                            Console.WriteLine($"    {skill.Description}");
                        }
                        Console.WriteLine($"    File: {skill.File}");
                        Console.WriteLine($"    Created: {skill.CreatedAt:yyyy-MM-dd HH:mm}");
                        Console.WriteLine();
                    }
                    break;

                case "run":
                    if (parts.Count < 4)
                    {
                        Console.WriteLine("Usage: /mcp skill run <name> [params_json]");
                        return;
                    }

                    var skillName = parts[3];
                    var paramsJson = parts.Count > 4 ? string.Join(" ", parts.Skip(4)) : "{}";

                    var skillInfo = SkillManager.GetSkill(skillName);
                    if (skillInfo == null)
                    {
                        Console.WriteLine($"Skill '{skillName}' not found. Use '/mcp skill list' to see available skills.");
                        return;
                    }

                    Console.WriteLine($"Running skill: {skillName}");
                    Console.WriteLine();

                    var result = await SkillManager.RunSkillAsync(skillName, paramsJson, ct);
                    if (result == null)
                    {
                        Console.WriteLine("Failed to load skill.");
                        return;
                    }

                    if (result.Success)
                    {
                        ConsoleHelpers.WithColor(ConsoleColor.Green, () => Console.WriteLine($"✓ Skill completed in {result.Duration.TotalMilliseconds:F0}ms"));
                        if (!string.IsNullOrEmpty(result.Result))
                        {
                            Console.WriteLine();
                            Console.WriteLine("Result:");
                            Console.WriteLine(result.Result);
                        }
                    }
                    else
                    {
                        ConsoleHelpers.WithColor(ConsoleColor.Red, () => Console.WriteLine($"✗ Skill failed: {result.Error}"));
                    }

                    if (result.ToolCalls.Count > 0)
                    {
                        Console.WriteLine();
                        ConsoleHelpers.WithColor(ConsoleColor.DarkGray, () => Console.WriteLine($"Tool calls: {result.ToolCalls.Count}"));
                    }
                    break;

                case "save":
                    if (parts.Count < 5)
                    {
                        Console.WriteLine("Usage: /mcp skill save <name> \"<code>\" [description]");
                        Console.WriteLine("Example: /mcp skill save count-files \"const files = await searchFiles('**/*'); return files.length;\"");
                        return;
                    }

                    var saveName = parts[3];
                    var codeStart = 4;
                    var code = string.Join(" ", parts.Skip(codeStart)).Trim('"');
                    var description = "";

                    // Check if last part is a description
                    if (parts.Count > 5 && parts[^1].StartsWith("--desc"))
                    {
                        var descIdx = parts.FindIndex(p => p.StartsWith("--desc"));
                        if (descIdx > 0 && descIdx + 1 < parts.Count)
                        {
                            description = parts[descIdx + 1].Trim('"');
                            code = string.Join(" ", parts.Skip(codeStart).Take(descIdx - codeStart)).Trim('"');
                        }
                    }

                    if (SkillManager.SaveSkill(saveName, code, description))
                    {
                        ConsoleHelpers.WithColor(ConsoleColor.Green, () => Console.WriteLine($"✓ Skill '{saveName}' saved."));
                    }
                    else
                    {
                        ConsoleHelpers.WithColor(ConsoleColor.Red, () => Console.WriteLine("Failed to save skill."));
                    }
                    break;

                case "delete":
                    if (parts.Count < 4)
                    {
                        Console.WriteLine("Usage: /mcp skill delete <name>");
                        return;
                    }

                    var deleteName = parts[3];
                    if (SkillManager.DeleteSkill(deleteName))
                    {
                        ConsoleHelpers.WithColor(ConsoleColor.Green, () => Console.WriteLine($"✓ Skill '{deleteName}' deleted."));
                    }
                    else
                    {
                        Console.WriteLine($"Skill '{deleteName}' not found.");
                    }
                    break;

                default:
                    Console.WriteLine("Unknown skill action. Available: list, run, save, delete");
                    break;
            }
        }

        /// <summary>
        /// Handle permissions subcommand
        /// </summary>
        private static void HandlePermissionsCommand(List<string> parts)
        {
            if (parts.Count < 3)
            {
                // Show current permissions
                Console.WriteLine("MCP Permission Settings:");
                Console.WriteLine($"  Permission Level: {McpConfig.Instance.PermissionLevel}");
                Console.WriteLine($"  Require Approval: {McpConfig.Instance.RequireApproval}");
                Console.WriteLine();
                Console.WriteLine("Permission Levels:");
                Console.WriteLine("  readonly   - Only read operations allowed");
                Console.WriteLine("  readwrite  - Read and write in project directory (default)");
                Console.WriteLine("  execute    - Can also run processes (dotnet, git)");
                Console.WriteLine("  full       - All permissions (use with caution)");
                Console.WriteLine();
                Console.WriteLine("Usage:");
                Console.WriteLine("  /mcp permissions set <level>   - Change permission level");
                Console.WriteLine("  /mcp permissions approval on|off - Toggle code approval");
                return;
            }

            var action = parts[2].ToLowerInvariant();

            switch (action)
            {
                case "set":
                    if (parts.Count < 4)
                    {
                        Console.WriteLine("Usage: /mcp permissions set <readonly|readwrite|execute|full>");
                        return;
                    }

                    var level = parts[3].ToLowerInvariant();
                    if (level is "readonly" or "readwrite" or "execute" or "full")
                    {
                        McpConfig.Instance.PermissionLevel = level;
                        McpConfig.SaveConfig();
                        ConsoleHelpers.WithColor(ConsoleColor.Green, () => Console.WriteLine($"✓ Permission level set to: {level}"));
                        
                        if (level == "full")
                        {
                            ConsoleHelpers.WithColor(ConsoleColor.Yellow, () => 
                                Console.WriteLine("Warning: Full permissions allow all operations. Use with caution."));
                        }
                    }
                    else
                    {
                        Console.WriteLine("Invalid level. Use: readonly, readwrite, execute, or full");
                    }
                    break;

                case "approval":
                    if (parts.Count < 4)
                    {
                        Console.WriteLine("Usage: /mcp permissions approval on|off");
                        return;
                    }

                    var approvalVal = parts[3].ToLowerInvariant();
                    if (approvalVal is "on" or "off")
                    {
                        McpConfig.Instance.RequireApproval = approvalVal == "on";
                        McpConfig.SaveConfig();
                        Console.WriteLine($"Code approval is now {(McpConfig.Instance.RequireApproval ? "ON" : "OFF")}");
                    }
                    else
                    {
                        Console.WriteLine("Usage: /mcp permissions approval on|off");
                    }
                    break;

                default:
                    Console.WriteLine("Unknown permissions action. Use: set, approval");
                    break;
            }
        }

        /// <summary>
        /// Handle audit subcommand
        /// </summary>
        private static void HandleAuditCommand(List<string> parts)
        {
            if (parts.Count < 3)
            {
                Console.WriteLine("MCP Audit Settings:");
                Console.WriteLine($"  Audit Logging: {(McpConfig.Instance.AuditLog ? "ON" : "OFF")}");
                Console.WriteLine();
                Console.WriteLine("Usage:");
                Console.WriteLine("  /mcp audit on|off    - Toggle audit logging");
                Console.WriteLine("  /mcp audit show      - Show recent audit log");
                return;
            }

            var action = parts[2].ToLowerInvariant();

            switch (action)
            {
                case "on":
                    McpConfig.Instance.AuditLog = true;
                    McpConfig.SaveConfig();
                    Console.WriteLine("Audit logging enabled.");
                    break;

                case "off":
                    McpConfig.Instance.AuditLog = false;
                    McpConfig.SaveConfig();
                    Console.WriteLine("Audit logging disabled.");
                    break;

                case "show":
                    Console.WriteLine("Recent MCP audit log:");
                    Console.WriteLine("(Check application logs for detailed audit trail)");
                    Console.WriteLine($"Log file: {Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "thuvu", "logs")}");
                    break;

                default:
                    Console.WriteLine("Unknown audit action. Use: on, off, show");
                    break;
            }
        }
    }
}
