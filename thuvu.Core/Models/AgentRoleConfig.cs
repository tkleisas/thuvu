using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace thuvu.Models
{
    /// <summary>
    /// Configuration for agent roles that enable sub-agent delegation.
    /// </summary>
    public class AgentRolesConfig
    {
        /// <summary>
        /// Whether sub-agent delegation is enabled. When false, uses traditional single-agent approach.
        /// </summary>
        public bool Enabled { get; set; } = false;
        
        /// <summary>
        /// Maximum delegation depth (0=main only, 1=one level of sub-agents, 2=sub-sub-agents allowed).
        /// </summary>
        public int MaxDepth { get; set; } = 2;
        
        /// <summary>
        /// Default context mode for sub-agents: 'full', 'summary', or 'selective'.
        /// </summary>
        public string DefaultContextMode { get; set; } = "full";
        
        /// <summary>
        /// List of available agent roles.
        /// </summary>
        public List<AgentRoleDefinition> Roles { get; set; } = new();
        
        /// <summary>
        /// Get a role definition by ID.
        /// </summary>
        public AgentRoleDefinition? GetRole(string roleId)
        {
            return Roles.FirstOrDefault(r => r.RoleId.Equals(roleId, StringComparison.OrdinalIgnoreCase));
        }
        
        /// <summary>
        /// Check if delegation is allowed from one role to another.
        /// Returns false if delegation is globally disabled.
        /// </summary>
        public bool CanDelegate(string fromRoleId, string toRoleId)
        {
            if (!Enabled)
                return false;
                
            var fromRole = GetRole(fromRoleId);
            if (fromRole == null || !fromRole.CanDelegate)
                return false;
            return fromRole.AllowedDelegations.Contains(toRoleId, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Definition of a single agent role.
    /// </summary>
    public class AgentRoleDefinition
    {
        /// <summary>
        /// Unique identifier for this role.
        /// </summary>
        public string RoleId { get; set; } = "";
        
        /// <summary>
        /// Display name for UI.
        /// </summary>
        public string DisplayName { get; set; } = "";
        
        /// <summary>
        /// Description of what this agent does.
        /// </summary>
        public string Description { get; set; } = "";
        
        /// <summary>
        /// Model ID to use for this role. If null, uses the current session's model.
        /// </summary>
        public string? ModelId { get; set; }
        
        /// <summary>
        /// Path to the system prompt file for this role.
        /// </summary>
        public string SystemPromptFile { get; set; } = "";
        
        /// <summary>
        /// Whether this role can delegate to sub-agents.
        /// </summary>
        public bool CanDelegate { get; set; }
        
        /// <summary>
        /// List of role IDs this agent can delegate to.
        /// </summary>
        public List<string> AllowedDelegations { get; set; } = new();
        
        /// <summary>
        /// Maximum number of iterations before bailout.
        /// </summary>
        public int MaxIterations { get; set; } = 30;
        
        /// <summary>
        /// Maximum duration in milliseconds before bailout.
        /// </summary>
        public long MaxDurationMs { get; set; } = 600000; // 10 minutes default
        
        /// <summary>
        /// Context mode: 'full', 'summary', or 'selective'.
        /// </summary>
        public string ContextMode { get; set; } = "full";
        
        /// <summary>
        /// Load the system prompt content from file.
        /// </summary>
        public string LoadSystemPrompt()
        {
            if (string.IsNullOrEmpty(SystemPromptFile))
                return "";
            
            var path = SystemPromptFile;
            if (!Path.IsPathRooted(path))
            {
                // Relative to executable directory
                path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
            }
            
            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }
            
            // Try relative to current directory
            path = Path.Combine(Directory.GetCurrentDirectory(), SystemPromptFile);
            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }
            
            AgentLogger.LogWarning("System prompt file not found: {Path}", SystemPromptFile);
            return "";
        }
    }

    /// <summary>
    /// Singleton instance for agent roles configuration.
    /// </summary>
    public static class AgentRolesRegistry
    {
        private static AgentRolesConfig? _instance;
        private static readonly object _lock = new();

        /// <summary>
        /// Gets the singleton instance, loading from config if needed.
        /// </summary>
        public static AgentRolesConfig Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        _instance ??= LoadFromConfig();
                    }
                }
                return _instance;
            }
        }

        /// <summary>
        /// Reload configuration from file.
        /// </summary>
        public static void Reload()
        {
            lock (_lock)
            {
                _instance = LoadFromConfig();
            }
        }

        private static AgentRolesConfig LoadFromConfig()
        {
            try
            {
                var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
                if (!File.Exists(configPath))
                {
                    configPath = "appsettings.json";
                }

                if (File.Exists(configPath))
                {
                    var config = new ConfigurationBuilder()
                        .AddJsonFile(configPath, optional: true)
                        .Build();

                    var rolesConfig = new AgentRolesConfig();
                    config.GetSection("AgentRoles").Bind(rolesConfig);
                    
                    AgentLogger.LogInfo("Loaded {Count} agent roles from config", rolesConfig.Roles.Count);
                    return rolesConfig;
                }
            }
            catch (Exception ex)
            {
                AgentLogger.LogError("Failed to load agent roles config: {Error}", ex.Message);
            }

            // Return default config with basic roles
            return CreateDefaultConfig();
        }

        private static AgentRolesConfig CreateDefaultConfig()
        {
            return new AgentRolesConfig
            {
                Enabled = false,  // Disabled by default, uses traditional single-agent approach
                MaxDepth = 2,
                DefaultContextMode = "full",
                Roles = new List<AgentRoleDefinition>
                {
                    new() { RoleId = "main", DisplayName = "Main Agent", CanDelegate = true, 
                            AllowedDelegations = new() { "planner", "coder", "tester", "reviewer", "debugger" },
                            MaxIterations = 50, MaxDurationMs = 1800000 },
                    new() { RoleId = "planner", DisplayName = "Planning Agent", CanDelegate = false, 
                            MaxIterations = 10, MaxDurationMs = 300000 },
                    new() { RoleId = "coder", DisplayName = "Coding Agent", CanDelegate = true,
                            AllowedDelegations = new() { "debugger" },
                            MaxIterations = 30, MaxDurationMs = 900000 },
                    new() { RoleId = "tester", DisplayName = "Testing Agent", CanDelegate = false,
                            MaxIterations = 20, MaxDurationMs = 600000 },
                    new() { RoleId = "reviewer", DisplayName = "Review Agent", CanDelegate = false,
                            MaxIterations = 10, MaxDurationMs = 300000 },
                    new() { RoleId = "debugger", DisplayName = "Debugging Agent", CanDelegate = false,
                            MaxIterations = 25, MaxDurationMs = 600000 }
                }
            };
        }
    }
}
