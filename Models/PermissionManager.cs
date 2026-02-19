using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace thuvu.Models
{
    public enum PermissionScope
    {
        Always,     // Persistent across sessions
        Session,    // For current session only
        Once        // For this single operation
    }

    public enum ToolRiskLevel
    {
        ReadOnly,   // Safe tools that only read data
        Write       // Tools that can modify files/system
    }

    public sealed class PermissionManager
    {
        private static readonly ConcurrentDictionary<string, PermissionScope> SessionPermissions = new();
        private static string? _currentRepoPath;
        
        /// <summary>
        /// Thread-local flag indicating we're inside MCP sandbox execution.
        /// When true, permissions are auto-granted since the outer execute_code already got permission.
        /// </summary>
        private static readonly AsyncLocal<bool> _inMcpContext = new();
        
        /// <summary>
        /// Set MCP context flag - permissions will be auto-granted for nested tool calls
        /// </summary>
        public static void EnterMcpContext() => _inMcpContext.Value = true;
        
        /// <summary>
        /// Clear MCP context flag
        /// </summary>
        public static void ExitMcpContext() => _inMcpContext.Value = false;
        
        /// <summary>
        /// Check if we're inside MCP context
        /// </summary>
        public static bool IsInMcpContext => _inMcpContext.Value;
        
        /// <summary>
        /// Custom permission prompt handler for TUI or other UI modes.
        /// Returns: 'A' for Always, 'S' for Session, 'O' for Once, 'N' for No/Cancel.
        /// If null, uses default console prompt.
        /// </summary>
        public static Func<string, string, char>? CustomPermissionPrompt { get; set; }
        
        /// <summary>
        /// Async permission prompt handler for Web UI.
        /// Takes toolName, argsJson, and returns a Task with the choice character.
        /// If set, takes precedence over CustomPermissionPrompt.
        /// </summary>
        public static Func<string, string, Task<char>>? AsyncPermissionPrompt { get; set; }

        // Tool categorization by risk level
        private static readonly HashSet<string> ReadOnlyTools = new(StringComparer.OrdinalIgnoreCase)
        {
            "search_files", "read_file", "git_status", "git_diff", "nuget_search",
            "rag_search", "rag_stats",
            "process_status", "process_read",  // Process read operations
            "code_query", "context_get", "index_stats"  // Code indexing read operations
        };

        private static readonly HashSet<string> WriteTools = new(StringComparer.OrdinalIgnoreCase)
        {
            "write_file", "apply_patch", "run_process", "dotnet_restore", "dotnet_build",
            "dotnet_test", "dotnet_run", "dotnet_new", "nuget_add",
            "rag_index", "rag_clear",
            "process_start", "process_write", "process_stop",  // Process management
            "code_index", "context_store", "index_clear"  // Code indexing write operations
        };
        
        // UI Automation tools - require global permission first
        private static readonly HashSet<string> UIReadOnlyTools = new(StringComparer.OrdinalIgnoreCase)
        {
            "ui_capture", "ui_list_windows", "ui_get_element", "ui_wait"
        };
        
        private static readonly HashSet<string> UIWriteTools = new(StringComparer.OrdinalIgnoreCase)
        {
            "ui_click", "ui_type", "ui_mouse_move", "ui_focus_window"
        };

        // Agent Communication tools - require global permission first
        private static readonly HashSet<string> AgentCommunicationTools = new(StringComparer.OrdinalIgnoreCase)
        {
            "agent_list", "agent_submit", "agent_status", "agent_result", "agent_cancel"
        };
        
        /// <summary>
        /// Global UI automation permission - must be granted before any UI tools can be used
        /// </summary>
        public static bool UIAutomationEnabled { get; private set; } = false;

        /// <summary>
        /// Global agent communication permission - must be granted before any agent tools can be used
        /// </summary>
        public static bool AgentCommunicationEnabled { get; private set; } = false;

        public static void SetCurrentRepoPath(string repoPath)
        {
            _currentRepoPath = repoPath;
        }
        
        /// <summary>
        /// Enable UI automation for the current session
        /// </summary>
        public static void EnableUIAutomation()
        {
            UIAutomationEnabled = true;
        }
        
        /// <summary>
        /// Check if a tool is a UI automation tool
        /// </summary>
        public static bool IsUIAutomationTool(string toolName)
        {
            return UIReadOnlyTools.Contains(toolName) || UIWriteTools.Contains(toolName);
        }

        /// <summary>
        /// Check if a tool is an agent communication tool
        /// </summary>
        public static bool IsAgentCommunicationTool(string toolName)
        {
            return AgentCommunicationTools.Contains(toolName);
        }

        public static ToolRiskLevel GetToolRiskLevel(string toolName)
        {
            if (ReadOnlyTools.Contains(toolName) || UIReadOnlyTools.Contains(toolName))
                return ToolRiskLevel.ReadOnly;
            
            if (WriteTools.Contains(toolName) || UIWriteTools.Contains(toolName))
                return ToolRiskLevel.Write;

            // Agent communication tools - agent_list is read-only, others are write
            if (toolName == "agent_list")
                return ToolRiskLevel.ReadOnly;
            if (AgentCommunicationTools.Contains(toolName))
                return ToolRiskLevel.Write;

            // Default to Write for unknown tools (safer)
            return ToolRiskLevel.Write;
        }

        public static bool CheckPermission(string toolName, string argsJson)
        {
            var riskLevel = GetToolRiskLevel(toolName);
            
            // Always allow read-only tools
            if (riskLevel == ToolRiskLevel.ReadOnly)
                return true;

            // Check for existing permissions
            string permissionKey = GetPermissionKey(toolName);
            
            // Check persistent permissions first
            if (AgentConfig.Config.ToolPermissions.ContainsKey(permissionKey))
            {
                AgentLogger.LogDebug("Tool {Tool} allowed by persistent permission (key: {Key})", toolName, permissionKey);
                return true;
            }

            // Check session permissions
            if (SessionPermissions.ContainsKey(permissionKey))
            {
                AgentLogger.LogDebug("Tool {Tool} allowed by session permission (key: {Key})", toolName, permissionKey);
                return true;
            }

            AgentLogger.LogDebug("Tool {Tool} requires permission prompt (key: {Key}, stored keys: {Keys})", 
                toolName, permissionKey, string.Join(", ", AgentConfig.Config.ToolPermissions.Keys));

            // Need to ask user
            return PromptForPermission(toolName, argsJson, permissionKey);
        }
        
        /// <summary>
        /// Async version of CheckPermission for Web UI
        /// </summary>
        public static async Task<bool> CheckPermissionAsync(string toolName, string argsJson)
        {
            // Check if this is a UI automation tool - requires global permission first
            if (IsUIAutomationTool(toolName))
            {
                if (!UIAutomationEnabled)
                {
                    // Prompt for global UI automation permission
                    var granted = await PromptForUIAutomationPermissionAsync(toolName);
                    if (!granted)
                    {
                        AgentLogger.LogDebug("UI automation permission denied for {Tool}", toolName);
                        return false;
                    }
                    UIAutomationEnabled = true;
                }
                
                // UI read-only tools are allowed after global permission
                if (UIReadOnlyTools.Contains(toolName))
                {
                    AgentLogger.LogDebug("Tool {Tool} allowed (UI read-only, global permission granted)", toolName);
                    return true;
                }
                
                // UI write tools fall through to normal per-tool permission check
            }

            // Check if this is an agent communication tool - requires global permission first
            if (IsAgentCommunicationTool(toolName))
            {
                if (!AgentCommunicationEnabled)
                {
                    // Prompt for global agent communication permission
                    var granted = await PromptForAgentCommunicationPermissionAsync(toolName);
                    if (!granted)
                    {
                        AgentLogger.LogDebug("Agent communication permission denied for {Tool}", toolName);
                        return false;
                    }
                    AgentCommunicationEnabled = true;
                }
                
                // agent_list is read-only, allowed after global permission
                if (toolName == "agent_list")
                {
                    AgentLogger.LogDebug("Tool {Tool} allowed (agent read-only, global permission granted)", toolName);
                    return true;
                }
                
                // Other agent tools fall through to normal per-tool permission check
            }
            
            var riskLevel = GetToolRiskLevel(toolName);
            
            // Always allow read-only tools (non-UI, non-agent)
            if (riskLevel == ToolRiskLevel.ReadOnly && !IsUIAutomationTool(toolName) && !IsAgentCommunicationTool(toolName))
                return true;
            
            // Auto-grant permissions when inside MCP context (nested tool calls)
            // The outer execute_code already got permission, so nested calls are allowed
            if (IsInMcpContext)
            {
                AgentLogger.LogDebug("Tool {Tool} auto-allowed in MCP context", toolName);
                return true;
            }

            // Check for existing permissions
            string permissionKey = GetPermissionKey(toolName);
            
            // Check persistent permissions first
            if (AgentConfig.Config.ToolPermissions.ContainsKey(permissionKey))
            {
                AgentLogger.LogDebug("Tool {Tool} allowed by persistent permission (key: {Key})", toolName, permissionKey);
                return true;
            }

            // Check session permissions
            if (SessionPermissions.ContainsKey(permissionKey))
            {
                AgentLogger.LogDebug("Tool {Tool} allowed by session permission (key: {Key})", toolName, permissionKey);
                return true;
            }

            AgentLogger.LogDebug("Tool {Tool} requires permission prompt (key: {Key})", toolName, permissionKey);

            // Use async handler if available (for Web UI)
            if (AsyncPermissionPrompt != null)
            {
                var choice = await AsyncPermissionPrompt(toolName, argsJson);
                return HandlePermissionChoice(choice, permissionKey);
            }
            
            // Fall back to sync prompt
            return PromptForPermission(toolName, argsJson, permissionKey);
        }

        private static string GetPermissionKey(string toolName)
        {
            var repoPath = _currentRepoPath ?? Directory.GetCurrentDirectory();
            // Normalize path for consistent key generation (Windows can have different casing)
            repoPath = Path.GetFullPath(repoPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var key = $"{repoPath}:{toolName}";
            return key;
        }

        private static bool PromptForPermission(string toolName, string argsJson, string permissionKey)
        {
            // Use custom handler if available (for TUI mode)
            if (CustomPermissionPrompt != null)
            {
                var choice = CustomPermissionPrompt(toolName, argsJson);
                return HandlePermissionChoice(choice, permissionKey);
            }
            
            Console.WriteLine();
            // Draw permission box
            var boxWidth = 60;
            var line = new string('═', boxWidth - 2);
            
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"╔{line}╗");
            Console.WriteLine($"║  ⚠  PERMISSION REQUIRED{new string(' ', boxWidth - 27)}║");
            Console.WriteLine($"╠{line}╣");
            Console.ResetColor();
            
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write("║ ");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write($"Tool: ");
            Console.ForegroundColor = ConsoleColor.White;
            var toolDisplay = toolName.Length > 45 ? toolName.Substring(0, 42) + "..." : toolName;
            Console.Write(toolDisplay);
            Console.WriteLine(new string(' ', boxWidth - 9 - toolDisplay.Length) + "║");
            
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"╠{line}╣");
            Console.ResetColor();
            
            // Truncate args for display
            var argsDisplay = argsJson.Length > 50 ? argsJson.Substring(0, 47) + "..." : argsJson;
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write("║ ");
            Console.Write($"Args: {argsDisplay}");
            Console.WriteLine(new string(' ', Math.Max(0, boxWidth - 9 - argsDisplay.Length)) + "║");
            
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"╠{line}╣");
            Console.ResetColor();
            
            // Options
            Console.Write("║ ");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("[A]");
            Console.ResetColor();
            Console.WriteLine($" Always for this repo{new string(' ', boxWidth - 26)}║");
            
            Console.Write("║ ");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write("[S]");
            Console.ResetColor();
            Console.WriteLine($" For this session{new string(' ', boxWidth - 22)}║");
            
            Console.Write("║ ");
            Console.ForegroundColor = ConsoleColor.Blue;
            Console.Write("[O]");
            Console.ResetColor();
            Console.WriteLine($" Once (this time only){new string(' ', boxWidth - 27)}║");
            
            Console.Write("║ ");
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Write("[N]");
            Console.ResetColor();
            Console.WriteLine($" No (cancel){new string(' ', boxWidth - 17)}║");
            
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"╚{line}╝");
            Console.ResetColor();
            
            Console.Write("Choice ");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write("[A/S/O/N]");
            Console.ResetColor();
            Console.Write(": ");

            while (true)
            {
                var key = Console.ReadKey(true);
                var choice = char.ToUpperInvariant(key.KeyChar);
                
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine(choice);
                Console.ResetColor();

                switch (choice)
                {
                    case 'A':
                        AgentConfig.Config.ToolPermissions[permissionKey] = true;
                        AgentConfig.SaveConfig();
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine(" ✓ Permission granted always for this repo");
                        Console.ResetColor();
                        return true;

                    case 'S':
                        SessionPermissions[permissionKey] = PermissionScope.Session;
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine(" ✓ Permission granted for this session");
                        Console.ResetColor();
                        return true;

                    case 'O':
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine(" ✓ Permission granted once");
                        Console.ResetColor();
                        return true;

                    case 'N':
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine(" ✗ Operation cancelled");
                        Console.ResetColor();
                        return false;

                    default:
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.Write("Invalid choice. ");
                        Console.ResetColor();
                        Console.Write("Please enter A, S, O, or N: ");
                        continue;
                }
            }
        }
        
        /// <summary>
        /// Handles the permission choice and updates permissions accordingly.
        /// Returns true if permission granted, false if denied.
        /// </summary>
        public static bool HandlePermissionChoice(char choice, string permissionKey)
        {
            switch (char.ToUpperInvariant(choice))
            {
                case 'A':
                    AgentConfig.Config.ToolPermissions[permissionKey] = true;
                    AgentConfig.SaveConfig();
                    return true;

                case 'S':
                    SessionPermissions[permissionKey] = PermissionScope.Session;
                    return true;

                case 'O':
                    return true;

                case 'N':
                default:
                    return false;
            }
        }
        
        /// <summary>
        /// Gets the permission key for a tool (for use with HandlePermissionChoice)
        /// </summary>
        public static string GetPermissionKeyForTool(string toolName)
        {
            return GetPermissionKey(toolName);
        }

        public static void ClearSessionPermissions()
        {
            SessionPermissions.Clear();
            UIAutomationEnabled = false; // Reset UI automation permission on session clear
        }
        
        /// <summary>
        /// Prompt for global UI automation permission (async version)
        /// </summary>
        private static async Task<bool> PromptForUIAutomationPermissionAsync(string triggeringTool)
        {
            // Use async handler if available (for Web UI)
            if (AsyncPermissionPrompt != null)
            {
                var choice = await AsyncPermissionPrompt("UI_AUTOMATION_GLOBAL", 
                    $"{{\"triggering_tool\":\"{triggeringTool}\",\"message\":\"UI Automation allows the agent to capture screenshots, control mouse/keyboard, and interact with windows. This gives the agent significant control over your system.\"}}");
                return char.ToUpperInvariant(choice) != 'N';
            }
            
            // Use custom handler if available (for TUI mode)
            if (CustomPermissionPrompt != null)
            {
                var choice = CustomPermissionPrompt("UI_AUTOMATION_GLOBAL", triggeringTool);
                return char.ToUpperInvariant(choice) != 'N';
            }
            
            // Console prompt
            Console.WriteLine();
            var boxWidth = 70;
            var line = new string('═', boxWidth - 2);
            
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"╔{line}╗");
            Console.WriteLine($"║  🖥️  UI AUTOMATION PERMISSION{new string(' ', boxWidth - 34)}║");
            Console.WriteLine($"╠{line}╣");
            Console.ResetColor();
            
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($"║ The agent wants to use UI automation tools.{new string(' ', boxWidth - 47)}║");
            Console.WriteLine($"║ This allows the agent to:{new string(' ', boxWidth - 28)}║");
            Console.ResetColor();
            
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"║   • Capture screenshots of your screen/windows{new string(' ', boxWidth - 50)}║");
            Console.WriteLine($"║   • Control mouse cursor and clicks{new string(' ', boxWidth - 38)}║");
            Console.WriteLine($"║   • Send keyboard input{new string(' ', boxWidth - 26)}║");
            Console.WriteLine($"║   • List and focus windows{new string(' ', boxWidth - 29)}║");
            Console.ResetColor();
            
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"╠{line}╣");
            Console.ResetColor();
            
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write($"║ Triggered by: ");
            Console.ForegroundColor = ConsoleColor.White;
            var toolDisplay = triggeringTool.Length > 45 ? triggeringTool.Substring(0, 42) + "..." : triggeringTool;
            Console.Write(toolDisplay);
            Console.WriteLine(new string(' ', boxWidth - 17 - toolDisplay.Length) + "║");
            
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"╠{line}╣");
            Console.ResetColor();
            
            Console.Write("║ ");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("[Y]");
            Console.ResetColor();
            Console.WriteLine($" Yes, enable UI automation for this session{new string(' ', boxWidth - 48)}║");
            
            Console.Write("║ ");
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Write("[N]");
            Console.ResetColor();
            Console.WriteLine($" No, deny access{new string(' ', boxWidth - 21)}║");
            
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"╚{line}╝");
            Console.ResetColor();
            
            Console.Write("Choice ");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write("[Y/N]");
            Console.ResetColor();
            Console.Write(": ");
            
            while (true)
            {
                var key = Console.ReadKey(true);
                var choice = char.ToUpperInvariant(key.KeyChar);
                
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine(choice);
                Console.ResetColor();
                
                if (choice == 'Y')
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine(" ✓ UI automation enabled for this session");
                    Console.ResetColor();
                    return true;
                }
                else if (choice == 'N')
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine(" ✗ UI automation denied");
                    Console.ResetColor();
                    return false;
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Write("Invalid choice. ");
                    Console.ResetColor();
                    Console.Write("Please enter Y or N: ");
                }
            }
        }

        private static async Task<bool> PromptForAgentCommunicationPermissionAsync(string triggeringTool)
        {
            // Use async handler if available (for Web UI)
            if (AsyncPermissionPrompt != null)
            {
                var choice = await AsyncPermissionPrompt("AGENT_COMMUNICATION_GLOBAL", 
                    $"{{\"triggering_tool\":\"{triggeringTool}\",\"message\":\"Agent Communication allows this agent to send tasks to and receive results from other agents. This enables multi-agent collaboration.\"}}");
                return char.ToUpperInvariant(choice) != 'N';
            }
            
            // Use custom handler if available (for TUI mode)
            if (CustomPermissionPrompt != null)
            {
                var choice = CustomPermissionPrompt("AGENT_COMMUNICATION_GLOBAL", triggeringTool);
                return char.ToUpperInvariant(choice) != 'N';
            }
            
            // Console prompt
            Console.WriteLine();
            var boxWidth = 70;
            var line = new string('═', boxWidth - 2);
            
            Console.ForegroundColor = ConsoleColor.Blue;
            Console.WriteLine($"╔{line}╗");
            Console.WriteLine($"║  🤝 AGENT COMMUNICATION PERMISSION{new string(' ', boxWidth - 39)}║");
            Console.WriteLine($"╠{line}╣");
            Console.ResetColor();
            
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($"║ The agent wants to communicate with other agents.{new string(' ', boxWidth - 52)}║");
            Console.WriteLine($"║ This allows the agent to:{new string(' ', boxWidth - 28)}║");
            Console.ResetColor();
            
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"║   • List known agents and their status{new string(' ', boxWidth - 41)}║");
            Console.WriteLine($"║   • Submit tasks/prompts to other agents{new string(' ', boxWidth - 43)}║");
            Console.WriteLine($"║   • Poll for job status and progress{new string(' ', boxWidth - 39)}║");
            Console.WriteLine($"║   • Retrieve results from completed jobs{new string(' ', boxWidth - 43)}║");
            Console.ResetColor();
            
            Console.ForegroundColor = ConsoleColor.Blue;
            Console.WriteLine($"╠{line}╣");
            Console.ResetColor();
            
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write($"║ Triggered by: ");
            Console.ForegroundColor = ConsoleColor.White;
            var toolDisplay = triggeringTool.Length > 45 ? triggeringTool.Substring(0, 42) + "..." : triggeringTool;
            Console.Write(toolDisplay);
            Console.WriteLine(new string(' ', boxWidth - 17 - toolDisplay.Length) + "║");
            
            Console.ForegroundColor = ConsoleColor.Blue;
            Console.WriteLine($"╠{line}╣");
            Console.ResetColor();
            
            Console.Write("║ ");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("[Y]");
            Console.ResetColor();
            Console.WriteLine($" Yes, enable agent communication for this session{new string(' ', boxWidth - 54)}║");
            
            Console.Write("║ ");
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Write("[N]");
            Console.ResetColor();
            Console.WriteLine($" No, deny access{new string(' ', boxWidth - 21)}║");
            
            Console.ForegroundColor = ConsoleColor.Blue;
            Console.WriteLine($"╚{line}╝");
            Console.ResetColor();
            
            Console.Write("Choice ");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write("[Y/N]");
            Console.ResetColor();
            Console.Write(": ");
            
            while (true)
            {
                var key = Console.ReadKey(true);
                var choice = char.ToUpperInvariant(key.KeyChar);
                
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine(choice);
                Console.ResetColor();
                
                if (choice == 'Y')
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine(" ✓ Agent communication enabled for this session");
                    Console.ResetColor();
                    return true;
                }
                else if (choice == 'N')
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine(" ✗ Agent communication denied");
                    Console.ResetColor();
                    return false;
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Write("Invalid choice. ");
                    Console.ResetColor();
                    Console.Write("Please enter Y or N: ");
                }
            }
        }

        // For testing purposes - simulate user choice without console interaction
        public static bool TestCheckPermission(string toolName, string argsJson, char simulatedUserChoice = 'N')
        {
            var riskLevel = GetToolRiskLevel(toolName);
            
            // Always allow read-only tools
            if (riskLevel == ToolRiskLevel.ReadOnly)
                return true;

            // Check for existing permissions
            string permissionKey = GetPermissionKey(toolName);
            
            // Check persistent permissions first
            if (AgentConfig.Config.ToolPermissions.ContainsKey(permissionKey))
                return true;

            // Check session permissions
            if (SessionPermissions.ContainsKey(permissionKey))
                return true;

            // Simulate user interaction for testing
            switch (char.ToUpperInvariant(simulatedUserChoice))
            {
                case 'A':
                    AgentConfig.Config.ToolPermissions[permissionKey] = true;
                    AgentConfig.SaveConfig();
                    return true;
                case 'S':
                    SessionPermissions[permissionKey] = PermissionScope.Session;
                    return true;
                case 'O':
                    return true;
                case 'N':
                default:
                    return false;
            }
        }
    }
}