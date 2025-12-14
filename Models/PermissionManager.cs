using System;
using System.Collections.Generic;
using System.IO;

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
        private static readonly Dictionary<string, PermissionScope> SessionPermissions = new();
        private static string? _currentRepoPath;
        
        /// <summary>
        /// Custom permission prompt handler for TUI or other UI modes.
        /// Returns: 'A' for Always, 'S' for Session, 'O' for Once, 'N' for No/Cancel.
        /// If null, uses default console prompt.
        /// </summary>
        public static Func<string, string, char>? CustomPermissionPrompt { get; set; }

        // Tool categorization by risk level
        private static readonly HashSet<string> ReadOnlyTools = new(StringComparer.OrdinalIgnoreCase)
        {
            "search_files", "read_file", "git_status", "git_diff", "nuget_search",
            "rag_search", "rag_stats"
        };

        private static readonly HashSet<string> WriteTools = new(StringComparer.OrdinalIgnoreCase)
        {
            "write_file", "apply_patch", "run_process", "dotnet_restore", "dotnet_build",
            "dotnet_test", "dotnet_run", "dotnet_new", "nuget_add",
            "rag_index", "rag_clear"
        };

        public static void SetCurrentRepoPath(string repoPath)
        {
            _currentRepoPath = repoPath;
        }

        public static ToolRiskLevel GetToolRiskLevel(string toolName)
        {
            if (ReadOnlyTools.Contains(toolName))
                return ToolRiskLevel.ReadOnly;
            
            if (WriteTools.Contains(toolName))
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
                return true;

            // Check session permissions
            if (SessionPermissions.ContainsKey(permissionKey))
                return true;

            // Need to ask user
            return PromptForPermission(toolName, argsJson, permissionKey);
        }

        private static string GetPermissionKey(string toolName)
        {
            var repoPath = _currentRepoPath ?? Directory.GetCurrentDirectory();
            return $"{repoPath}:{toolName}";
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