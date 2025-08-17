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

        // Tool categorization by risk level
        private static readonly HashSet<string> ReadOnlyTools = new(StringComparer.OrdinalIgnoreCase)
        {
            "search_files", "read_file", "git_status", "git_diff", "nuget_search"
        };

        private static readonly HashSet<string> WriteTools = new(StringComparer.OrdinalIgnoreCase)
        {
            "write_file", "apply_patch", "run_process", "dotnet_restore", "dotnet_build",
            "dotnet_test", "dotnet_run", "dotnet_new", "nuget_add"
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
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"⚠️  Permission Required");
            Console.ResetColor();
            Console.WriteLine($"Tool '{toolName}' wants to perform a write operation.");
            Console.WriteLine($"Arguments: {argsJson}");
            Console.WriteLine();
            Console.WriteLine("Allow this operation?");
            Console.WriteLine("  [A] Always for this repo");
            Console.WriteLine("  [S] For this session");
            Console.WriteLine("  [O] Once (this time only)");
            Console.WriteLine("  [N] No (cancel)");
            Console.WriteLine();
            Console.Write("Choice [A/S/O/N]: ");

            while (true)
            {
                var key = Console.ReadKey(true);
                var choice = char.ToUpperInvariant(key.KeyChar);
                
                Console.WriteLine(choice);

                switch (choice)
                {
                    case 'A':
                        // Store persistent permission
                        AgentConfig.Config.ToolPermissions[permissionKey] = true;
                        AgentConfig.SaveConfig();
                        Console.WriteLine("✓ Permission granted always for this repo");
                        return true;

                    case 'S':
                        // Store session permission
                        SessionPermissions[permissionKey] = PermissionScope.Session;
                        Console.WriteLine("✓ Permission granted for this session");
                        return true;

                    case 'O':
                        Console.WriteLine("✓ Permission granted once");
                        return true;

                    case 'N':
                        Console.WriteLine("❌ Operation cancelled");
                        return false;

                    default:
                        Console.Write("Invalid choice. Please enter A, S, O, or N: ");
                        continue;
                }
            }
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