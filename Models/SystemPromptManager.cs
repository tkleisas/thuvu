using System.Text;

namespace thuvu.Models
{
    /// <summary>
    /// Manages system prompts for different models and contexts
    /// </summary>
    public class SystemPromptManager
    {
        private static SystemPromptManager? _instance;
        public static SystemPromptManager Instance => _instance ??= new SystemPromptManager();
        
        private readonly Dictionary<string, string> _templateCache = new();
        private readonly string _promptsDirectory;
        
        public SystemPromptManager()
        {
            // Look for prompts directory in multiple locations
            var configDir = Path.GetDirectoryName(AgentConfig.GetConfigPath()) ?? "";
            var candidates = new[]
            {
                Path.Combine(Directory.GetCurrentDirectory(), "prompts"),
                Path.Combine(AppContext.BaseDirectory, "prompts"),
                Path.Combine(configDir, "prompts"),
                Path.Combine(AgentConfig.GetWorkDirectory(), "..", "prompts")
            };
            
            _promptsDirectory = candidates.FirstOrDefault(Directory.Exists) 
                ?? Path.Combine(Directory.GetCurrentDirectory(), "prompts");
        }
        
        /// <summary>
        /// Get the system prompt for a specific model
        /// </summary>
        public string GetSystemPrompt(ModelEndpoint model, bool mcpEnabled = false)
        {
            // 1. Check for custom prompt on the model
            if (!string.IsNullOrWhiteSpace(model.SystemPrompt))
            {
                return ResolvePrompt(model.SystemPrompt);
            }
            
            // 2. Check for template reference
            if (!string.IsNullOrWhiteSpace(model.SystemPromptTemplate))
            {
                var template = GetTemplate(model.SystemPromptTemplate);
                if (!string.IsNullOrEmpty(template))
                {
                    return ApplyVariables(template, model, mcpEnabled);
                }
            }
            
            // 3. Fall back to default based on model purpose
            return GetDefaultPrompt(model, mcpEnabled);
        }
        
        /// <summary>
        /// Get the system prompt for the current active model
        /// </summary>
        public string GetCurrentSystemPrompt(bool mcpEnabled = false)
        {
            var modelId = AgentConfig.Config.Model;
            var model = ModelRegistry.Instance.GetModel(modelId);
            
            if (model != null)
            {
                return GetSystemPrompt(model, mcpEnabled);
            }
            
            // Fall back to default
            return McpSystemPrompts.GetSystemPrompt(mcpEnabled);
        }
        
        /// <summary>
        /// Get a label describing the source of the current system prompt
        /// </summary>
        public string GetCurrentSystemPromptLabel(bool mcpEnabled = false)
        {
            var modelId = AgentConfig.Config.Model;
            var model = ModelRegistry.Instance.GetModel(modelId);
            
            if (model != null)
            {
                if (!string.IsNullOrWhiteSpace(model.SystemPrompt))
                {
                    if (model.SystemPrompt.StartsWith("@"))
                        return $"File: {model.SystemPrompt.Substring(1).Trim()}";
                    return "Custom (inline)";
                }
                
                if (!string.IsNullOrWhiteSpace(model.SystemPromptTemplate))
                    return $"Template: {model.SystemPromptTemplate}";
            }
            
            // Check if default.md template exists
            var defaultTemplate = GetTemplate("default");
            if (!string.IsNullOrEmpty(defaultTemplate))
                return "Template: default";
            
            return mcpEnabled ? "Default (MCP)" : "Default (Standard)";
        }
        
        /// <summary>
        /// Resolve a prompt value - could be inline text or file reference
        /// </summary>
        private string ResolvePrompt(string promptValue)
        {
            if (string.IsNullOrWhiteSpace(promptValue))
                return "";
                
            // Check if it's a file reference (starts with @)
            if (promptValue.StartsWith("@"))
            {
                var filePath = promptValue.Substring(1).Trim();
                return LoadPromptFromFile(filePath);
            }
            
            // Inline prompt
            return promptValue;
        }
        
        /// <summary>
        /// Load a prompt from a file
        /// </summary>
        private string LoadPromptFromFile(string relativePath)
        {
            // Try absolute path first
            if (Path.IsPathRooted(relativePath) && File.Exists(relativePath))
            {
                return File.ReadAllText(relativePath);
            }
            
            // Try relative to prompts directory
            var fullPath = Path.Combine(_promptsDirectory, relativePath);
            if (File.Exists(fullPath))
            {
                return File.ReadAllText(fullPath);
            }
            
            // Try relative to current directory
            fullPath = Path.Combine(Directory.GetCurrentDirectory(), relativePath);
            if (File.Exists(fullPath))
            {
                return File.ReadAllText(fullPath);
            }
            
            SessionLogger.Instance.LogInfo($"Prompt file not found: {relativePath}");
            return "";
        }
        
        /// <summary>
        /// Get a named template
        /// </summary>
        public string GetTemplate(string templateName)
        {
            if (_templateCache.TryGetValue(templateName, out var cached))
            {
                return cached;
            }
            
            // Try to load from file
            var filePath = Path.Combine(_promptsDirectory, $"{templateName}.md");
            if (File.Exists(filePath))
            {
                var content = File.ReadAllText(filePath);
                _templateCache[templateName] = content;
                return content;
            }
            
            // Try built-in templates
            var builtIn = GetBuiltInTemplate(templateName);
            if (!string.IsNullOrEmpty(builtIn))
            {
                _templateCache[templateName] = builtIn;
                return builtIn;
            }
            
            return "";
        }
        
        /// <summary>
        /// Get built-in template by name
        /// </summary>
        private string GetBuiltInTemplate(string name) => name.ToLowerInvariant() switch
        {
            "coding" => CodingTemplate,
            "thinking" => ThinkingTemplate,
            "general" => GeneralTemplate,
            "minimal" => MinimalTemplate,
            _ => ""
        };
        
        /// <summary>
        /// Apply variable substitution to a template
        /// </summary>
        private string ApplyVariables(string template, ModelEndpoint model, bool mcpEnabled)
        {
            var sb = new StringBuilder(template);
            
            // Platform info
            sb.Replace("{{PLATFORM}}", GetPlatformInfo());
            sb.Replace("{{OS}}", System.Runtime.InteropServices.RuntimeInformation.OSDescription);
            sb.Replace("{{PATH_SEPARATOR}}", Path.DirectorySeparatorChar.ToString());
            sb.Replace("{{WORK_DIR}}", AgentConfig.GetWorkDirectory());
            
            // Model info
            sb.Replace("{{MODEL_ID}}", model.ModelId);
            sb.Replace("{{MODEL_NAME}}", model.DisplayName);
            sb.Replace("{{MAX_CONTEXT}}", model.MaxContextLength.ToString());
            sb.Replace("{{MAX_OUTPUT}}", model.MaxOutputTokens.ToString());
            
            // Feature flags
            sb.Replace("{{MCP_ENABLED}}", mcpEnabled.ToString().ToLower());
            sb.Replace("{{TOOLS_ENABLED}}", model.SupportsTools.ToString().ToLower());
            sb.Replace("{{IS_THINKING_MODEL}}", model.IsThinkingModel.ToString().ToLower());
            
            // Conditional sections
            if (mcpEnabled)
            {
                sb.Replace("{{#MCP}}", "").Replace("{{/MCP}}", "");
                // Remove non-MCP sections
                RemoveSection(sb, "{{#STANDARD}}", "{{/STANDARD}}");
            }
            else
            {
                sb.Replace("{{#STANDARD}}", "").Replace("{{/STANDARD}}", "");
                // Remove MCP sections
                RemoveSection(sb, "{{#MCP}}", "{{/MCP}}");
            }
            
            return sb.ToString();
        }
        
        private void RemoveSection(StringBuilder sb, string startTag, string endTag)
        {
            var str = sb.ToString();
            while (true)
            {
                var start = str.IndexOf(startTag);
                if (start < 0) break;
                
                var end = str.IndexOf(endTag, start);
                if (end < 0) break;
                
                str = str.Remove(start, end - start + endTag.Length);
            }
            sb.Clear();
            sb.Append(str);
        }
        
        /// <summary>
        /// Get default prompt based on model configuration
        /// </summary>
        private string GetDefaultPrompt(ModelEndpoint model, bool mcpEnabled)
        {
            // Thinking models get a simpler prompt (they don't use tools)
            if (model.IsThinkingModel)
            {
                return ApplyVariables(ThinkingTemplate, model, mcpEnabled);
            }
            
            // Try loading default.md template from prompts directory
            var defaultTemplate = GetTemplate("default");
            if (!string.IsNullOrEmpty(defaultTemplate))
            {
                return ApplyVariables(defaultTemplate, model, mcpEnabled);
            }
            
            // Fall back to hardcoded MCP system prompts
            return McpSystemPrompts.GetSystemPrompt(mcpEnabled);
        }
        
        private static string GetPlatformInfo()
        {
            var platform = OperatingSystem.IsWindows() ? "Windows" : 
                           OperatingSystem.IsLinux() ? "Linux" : 
                           OperatingSystem.IsMacOS() ? "macOS" : "Unknown";
            var shellHint = OperatingSystem.IsWindows() 
                ? "Use PowerShell or cmd syntax, NOT bash/shell commands" 
                : "Use bash/shell commands";
            
            return $@"## Platform Information
- Operating System: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}
- Platform: {platform}
- {shellHint}
- Path separator: {Path.DirectorySeparatorChar}
- Working directory: {AgentConfig.GetWorkDirectory()}
";
        }
        
        /// <summary>
        /// List all available templates
        /// </summary>
        public List<string> GetAvailableTemplates()
        {
            var templates = new List<string> { "coding", "thinking", "general", "minimal" };
            
            if (Directory.Exists(_promptsDirectory))
            {
                var files = Directory.GetFiles(_promptsDirectory, "*.md")
                    .Select(f => Path.GetFileNameWithoutExtension(f))
                    .Where(n => !templates.Contains(n.ToLower()));
                templates.AddRange(files);
            }
            
            return templates;
        }
        
        /// <summary>
        /// Save a custom template
        /// </summary>
        public void SaveTemplate(string name, string content)
        {
            Directory.CreateDirectory(_promptsDirectory);
            var filePath = Path.Combine(_promptsDirectory, $"{name}.md");
            File.WriteAllText(filePath, content);
            _templateCache[name] = content;
        }
        
        /// <summary>
        /// Clear the template cache
        /// </summary>
        public void ClearCache()
        {
            _templateCache.Clear();
        }
        
        #region Built-in Templates
        
        private const string CodingTemplate = @"{{PLATFORM}}

You are a skilled coding assistant. Your goal is to help with software development tasks efficiently and accurately.

## Core Principles
- Prefer using tools over guessing; never invent file paths
- Read files before modifying them
- Run tests after making changes
- Keep changes minimal and focused

{{#STANDARD}}
## Workflow for Code Changes
1. Use `read_file` to examine existing code
2. Use `apply_patch` for minimal unified diffs
3. Run `dotnet_build` to verify compilation
4. Run `dotnet_test` to verify functionality
5. If tests pass, commit with a clear message

## Creating New Files
- Use `write_file` with `create_intermediate_dirs=true` for new files
- Use `dotnet_new` for .NET project scaffolding
- Always build after creating files to verify

## Running Applications
- For quick commands: use `run_process` or `dotnet_run` (blocking)
- For long-running apps (servers, GUI): use `process_start` (background)
  - `process_read` to check output
  - `process_write` to send input
  - `process_stop` to terminate

## UI Automation (for visual debugging)
- `ui_capture`: Screenshot with optional vision analysis
- `ui_click`, `ui_type`: Interact with UI elements
- `ui_get_element`, `ui_wait`: Inspect and wait for UI

## Code Indexing & Context
- `code_index`: Index source files to enable symbol search (use path '.' for current project)
- `code_query`: Search symbols by name, kind (class/method/property), or file
- `index_stats`: Check if index is populated before querying
- `context_store`: Save decisions, patterns, notes for later retrieval
- `context_get`: Retrieve stored context by key or category
- **Workflow**: Run `index_stats` first → if empty, run `code_index` with path '.' → then use `code_query` to find symbols
- Indexing is incremental — re-running skips unchanged files
{{/STANDARD}}

{{#MCP}}
## MCP Code Execution
Write TypeScript code to batch operations efficiently:
```typescript
import { readFile, writeFile, searchFiles } from './servers/filesystem';
import { build, test } from './servers/dotnet';
import { start, read, stop } from './servers/process';

// Batch file operations
const files = await searchFiles('**/*.cs');
const contents = await Promise.all(files.map(f => readFile(f)));

// Background process for long-running apps
const session = await start('dotnet', ['run']);
await sleep(2000);
const output = await read(session.session_id);
await stop(session.session_id);
```
{{/MCP}}

## Important Notes
- Quick commands block until completion
- Long-running apps should use process_start for background execution
- If a tool fails repeatedly, try a different approach

Say 'thuvu Finished Tasks' when complete.";

        private const string ThinkingTemplate = @"{{PLATFORM}}

You are a thoughtful reasoning assistant. Take your time to analyze problems deeply before providing solutions.

## Your Approach
1. **Understand**: Carefully read and understand the request
2. **Analyze**: Break down complex problems into components
3. **Reason**: Think through each aspect systematically
4. **Synthesize**: Combine insights into a coherent solution
5. **Verify**: Check your reasoning for errors or gaps

## Guidelines
- Show your reasoning process when helpful
- Consider edge cases and potential issues
- Acknowledge uncertainty when appropriate
- Provide clear, actionable recommendations

## Context
- Model: {{MODEL_NAME}}
- Working Directory: {{WORK_DIR}}

Focus on providing well-reasoned, thoughtful responses.";

        private const string GeneralTemplate = @"{{PLATFORM}}

You are a helpful AI assistant with access to various tools for file operations, code building, and more.

## Available Capabilities
- File operations (read, write, search, patch)
- Code building and testing (.NET)
- Git operations (status, diff)
- Package management (NuGet)

## Guidelines
- Use tools to gather information before answering
- Be concise but thorough
- Ask clarifying questions when needed
- Verify your work with builds/tests when making code changes

Say 'thuvu Finished Tasks' when you've completed the requested work.";

        private const string MinimalTemplate = @"You are a coding assistant. Use the available tools to help with development tasks. Be concise.";

        #endregion
    }
}
