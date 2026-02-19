using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace thuvu.Models
{
    /// <summary>
    /// Metadata for a saved skill
    /// </summary>
    public class SkillMetadata
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("version")]
        public string Version { get; set; } = "1.0.0";

        [JsonPropertyName("file")]
        public string File { get; set; } = string.Empty;

        [JsonPropertyName("createdAt")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [JsonPropertyName("parameters")]
        public Dictionary<string, SkillParameter> Parameters { get; set; } = new();
    }

    /// <summary>
    /// Parameter definition for a skill
    /// </summary>
    public class SkillParameter
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "string";

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("required")]
        public bool Required { get; set; } = false;

        [JsonPropertyName("default")]
        public object? Default { get; set; }
    }

    /// <summary>
    /// Skill registry index
    /// </summary>
    public class SkillRegistry
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = "1.0.0";

        [JsonPropertyName("skills")]
        public List<SkillMetadata> Skills { get; set; } = new();
    }

    /// <summary>
    /// Manages saved TypeScript skills
    /// </summary>
    public static class SkillManager
    {
        private static string SkillsDirectory => 
            Path.Combine(Directory.GetCurrentDirectory(), McpConfig.Instance.SkillsDirectory);

        private static string IndexPath => 
            Path.Combine(SkillsDirectory, "index.json");

        /// <summary>
        /// Ensure skills directory exists
        /// </summary>
        public static void EnsureDirectoryExists()
        {
            Directory.CreateDirectory(SkillsDirectory);
            if (!File.Exists(IndexPath))
            {
                var registry = new SkillRegistry();
                SaveRegistry(registry);
            }
        }

        /// <summary>
        /// Load skill registry
        /// </summary>
        public static SkillRegistry LoadRegistry()
        {
            EnsureDirectoryExists();
            try
            {
                var json = File.ReadAllText(IndexPath);
                return JsonSerializer.Deserialize<SkillRegistry>(json) ?? new SkillRegistry();
            }
            catch
            {
                return new SkillRegistry();
            }
        }

        /// <summary>
        /// Save skill registry
        /// </summary>
        public static void SaveRegistry(SkillRegistry registry)
        {
            EnsureDirectoryExists();
            var json = JsonSerializer.Serialize(registry, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(IndexPath, json);
        }

        /// <summary>
        /// List all available skills
        /// </summary>
        public static List<SkillMetadata> ListSkills()
        {
            return LoadRegistry().Skills;
        }

        /// <summary>
        /// Get a skill by name
        /// </summary>
        public static SkillMetadata? GetSkill(string name)
        {
            var registry = LoadRegistry();
            return registry.Skills.Find(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Save a new skill
        /// </summary>
        public static bool SaveSkill(string name, string code, string description = "")
        {
            EnsureDirectoryExists();

            // Sanitize name for filename
            var safeName = SanitizeFileName(name);
            var fileName = $"{safeName}.ts";
            var filePath = Path.Combine(SkillsDirectory, fileName);

            // Add metadata export if not present
            if (!code.Contains("export const metadata"))
            {
                var metadataBlock = $@"
export const metadata = {{
  name: '{name}',
  description: '{description.Replace("'", "\\'")}',
  version: '1.0.0'
}};

";
                code = metadataBlock + code;
            }

            // Save file
            File.WriteAllText(filePath, code);

            // Update registry
            var registry = LoadRegistry();
            
            // Remove existing skill with same name
            registry.Skills.RemoveAll(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            
            // Add new skill
            registry.Skills.Add(new SkillMetadata
            {
                Name = name,
                Description = description,
                File = fileName,
                CreatedAt = DateTime.UtcNow
            });

            SaveRegistry(registry);
            return true;
        }

        /// <summary>
        /// Delete a skill
        /// </summary>
        public static bool DeleteSkill(string name)
        {
            var skill = GetSkill(name);
            if (skill == null) return false;

            var filePath = Path.Combine(SkillsDirectory, skill.File);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            var registry = LoadRegistry();
            registry.Skills.RemoveAll(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            SaveRegistry(registry);

            return true;
        }

        /// <summary>
        /// Load skill code
        /// </summary>
        public static string? LoadSkillCode(string name)
        {
            var skill = GetSkill(name);
            if (skill == null) return null;

            var filePath = Path.Combine(SkillsDirectory, skill.File);
            if (!File.Exists(filePath)) return null;

            return File.ReadAllText(filePath);
        }

        /// <summary>
        /// Run a skill with MCP executor
        /// </summary>
        public static async Task<McpExecutionResult?> RunSkillAsync(
            string name, 
            string? paramsJson = null,
            CancellationToken ct = default)
        {
            var code = LoadSkillCode(name);
            if (code == null) return null;

            // Wrap code to execute the skill
            var executeCode = $@"
{code}

// Execute the skill
const result = await execute({paramsJson ?? "{}"});
return result;
";

            using var executor = new McpCodeExecutor();
            return await executor.ExecuteAsync(executeCode, ct);
        }

        /// <summary>
        /// Sanitize name for use as filename
        /// </summary>
        private static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var safe = name.ToLowerInvariant();
            
            foreach (var c in invalid)
            {
                safe = safe.Replace(c, '-');
            }
            
            // Replace spaces with dashes
            safe = safe.Replace(' ', '-');
            
            // Remove multiple dashes
            while (safe.Contains("--"))
            {
                safe = safe.Replace("--", "-");
            }

            return safe.Trim('-');
        }
    }
}
