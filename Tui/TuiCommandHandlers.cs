using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using thuvu.Models;
using CodingAgent;
using thuvu.Tools;

namespace thuvu.Tui
{
    /// <summary>
    /// Handles slash commands for the TUI interface
    /// </summary>
    public class TuiCommandHandlers
    {
        private readonly HttpClient _http;
        private readonly Action<string, bool> _appendText;
        private readonly Action _updateStatus;
        private readonly CancellationToken _appCancellationToken;
        
        public TuiCommandHandlers(
            HttpClient http,
            Action<string, bool> appendText,
            Action updateStatus,
            CancellationToken appCancellationToken)
        {
            _http = http;
            _appendText = appendText;
            _updateStatus = updateStatus;
            _appCancellationToken = appCancellationToken;
        }
        
        public async Task<bool> HandleCommandAsync(string command, List<ChatMessage> messages)
        {
            // Simple commands
            if (command.Equals("/help", StringComparison.OrdinalIgnoreCase))
            {
                ShowHelp();
                return true;
            }

            if (command.StartsWith("/clear", StringComparison.OrdinalIgnoreCase))
            {
                messages.Clear();
                messages.Add(new ChatMessage("system", SystemPromptManager.Instance.GetCurrentSystemPrompt(McpConfig.Instance.Enabled)));
                _appendText("[OK] Conversation cleared.", false);
                return true;
            }

            if (command.StartsWith("/stream", StringComparison.OrdinalIgnoreCase))
            {
                var arg = command.Length > 7 ? command[7..].Trim() : "";
                if (string.Equals(arg, "on", StringComparison.OrdinalIgnoreCase))
                    AgentConfig.Config.Stream = true;
                else if (string.Equals(arg, "off", StringComparison.OrdinalIgnoreCase))
                    AgentConfig.Config.Stream = false;
                else
                {
                    _appendText("Usage: /stream on|off", true);
                    return true;
                }
                _appendText($"[OK] Streaming: {(AgentConfig.Config.Stream ? "ON" : "OFF")}", false);
                _updateStatus();
                return true;
            }

            if (command.StartsWith("/models", StringComparison.OrdinalIgnoreCase))
            {
                HandleModelsCommand(command);
                return true;
            }
            
            // Commands that delegate to CommandHandlers
            if (command.StartsWith("/config", StringComparison.OrdinalIgnoreCase))
            {
                await CommandHandlers.HandleConfigCommandAsync(command, _http, _appCancellationToken);
                return true;
            }
            
            if (command.StartsWith("/set", StringComparison.OrdinalIgnoreCase))
            {
                await CommandHandlers.HandleSetCommandAsync(command, _http, _appCancellationToken);
                _updateStatus();
                return true;
            }
            
            if (command.StartsWith("/diff", StringComparison.OrdinalIgnoreCase))
            {
                await CommandHandlers.HandleDiffCommandAsync(command, _appCancellationToken, _appendText);
                return true;
            }
            
            if (command.StartsWith("/test", StringComparison.OrdinalIgnoreCase))
            {
                _appendText("Running tests...", false);
                await CommandHandlers.HandleTestCommandAsync(command, _appCancellationToken, _appendText);
                return true;
            }
            
            if (command.StartsWith("/run ", StringComparison.OrdinalIgnoreCase))
            {
                await CommandHandlers.HandleRunCommandAsync(command, _appCancellationToken, _appendText);
                return true;
            }
            
            if (command.StartsWith("/commit", StringComparison.OrdinalIgnoreCase))
            {
                await CommandHandlers.HandleCommitCommandAsync(command, _appCancellationToken);
                _appendText("Commit completed.", false);
                return true;
            }
            
            if (command.StartsWith("/push", StringComparison.OrdinalIgnoreCase))
            {
                await GitCommandHandlers.HandlePushCommandAsync(command, _appCancellationToken);
                _appendText("Push completed.", false);
                return true;
            }
            
            if (command.StartsWith("/pull", StringComparison.OrdinalIgnoreCase))
            {
                await GitCommandHandlers.HandlePullCommandAsync(command, _appCancellationToken);
                _appendText("Pull completed.", false);
                return true;
            }
            
            if (command.StartsWith("/rag", StringComparison.OrdinalIgnoreCase))
            {
                await RagCommandHandlers.HandleRagCommandAsync(command, _appCancellationToken);
                return true;
            }
            
            if (command.StartsWith("/mcp", StringComparison.OrdinalIgnoreCase))
            {
                await McpCommandHandlers.HandleMcpCommandAsync(command, _appCancellationToken);
                return true;
            }
            
            if (command.StartsWith("/health", StringComparison.OrdinalIgnoreCase))
            {
                _appendText("Running health check...", false);
                await HealthCheck.RunAllChecksAsync(_http);
                return true;
            }
            
            if (command.StartsWith("/status", StringComparison.OrdinalIgnoreCase))
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"Messages: {messages.Count}");
                sb.AppendLine($"Model: {AgentConfig.Config.Model}");
                sb.AppendLine($"Host: {AgentConfig.Config.HostUrl}");
                sb.AppendLine($"Stream: {AgentConfig.Config.Stream}");
                sb.AppendLine($"Work Dir: {Directory.GetCurrentDirectory()}");
                _appendText(sb.ToString(), false);
                return true;
            }
            
            if (command.StartsWith("/tokens", StringComparison.OrdinalIgnoreCase))
            {
                int totalTokens = 0;
                foreach (var msg in messages)
                {
                    totalTokens += (msg.Content?.Length ?? 0) / 4;
                }
                _appendText($"Estimated tokens: ~{totalTokens} (based on {messages.Count} messages)", false);
                return true;
            }

            if (command.StartsWith("/summarize", StringComparison.OrdinalIgnoreCase))
            {
                _appendText("Summarizing conversation...", false);
                try
                {
                    var (success, _) = await AgentLoop.SummarizeConversationAsync(
                        _http, AgentConfig.Config.Model, messages, CancellationToken.None,
                        s => _appendText($"  {s}", false));
                    
                    if (success)
                        _appendText("✓ Conversation summarized successfully.", false);
                    else
                        _appendText("✗ Summarization failed or not enough messages.", false);
                }
                catch (Exception ex)
                {
                    _appendText($"✗ Summarization error: {ex.Message}", true);
                }
                return true;
            }
            
            return false; // Command not handled
        }

        private void ShowHelp()
        {
            _appendText(@"
T.H.U.V.U. HELP
===============
Ctrl+Enter    Send message
/             Command autocomplete
@             File autocomplete (file: or dir: prefix)
Tab           Select autocomplete
Esc           Close autocomplete / Cancel

COMMANDS
--------
/help           Show this help
/exit           Quit
/clear          Reset conversation
/status         Show session status
/tokens         Estimate token usage

CONFIGURATION
-------------
/config         Show current configuration
/set KEY VALUE  Change setting
/stream on|off  Toggle streaming
/models list    List available models
/models use ID  Switch model

DEVELOPMENT
-----------
/diff           Show git diff
/test           Run dotnet tests
/run CMD        Run whitelisted command
/commit MSG     Commit with test gate
/push           Safe push with checks
/pull           Safe pull with autostash

ORCHESTRATION
-------------
/plan DESC      Create execution plan from task description
/orchestrate    Execute plan with multiple agents
  --agents N    Number of agents (1-8)
  --reset       Start fresh (reset all tasks)
  --retry       Retry failed tasks
  --skip        Skip failed dependencies (proceed anyway)
  --plan FILE   Use specific plan file

ADVANCED
--------
/rag            RAG operations (index, search, stats, clear)
/mcp            MCP code execution
/health         Run health checks

Permission prompts appear for write operations.
[A]lways | [S]ession | [O]nce | [N]o
", false);
        }

        private void HandleModelsCommand(string command)
        {
            var args = ConsoleHelpers.TokenizeArgs(command);
            var subCommand = args.Count > 1 ? args[1].ToLowerInvariant() : "list";

            switch (subCommand)
            {
                case "list":
                    _appendText("Models:", false);
                    foreach (var m in ModelRegistry.Instance.Models)
                    {
                        var isDefault = m.ModelId == ModelRegistry.Instance.DefaultModelId ? " *" : "";
                        _appendText($"  {(m.Enabled ? "[+]" : "[-]")} {m.DisplayName ?? m.ModelId}{isDefault}", false);
                    }
                    break;

                case "use":
                    if (args.Count < 3)
                    {
                        _appendText("Usage: /models use <model-id>", true);
                        return;
                    }
                    var model = ModelRegistry.Instance.GetModel(args[2]);
                    if (model == null)
                    {
                        _appendText($"Model '{args[2]}' not found", true);
                        return;
                    }
                    ModelRegistry.Instance.DefaultModelId = model.ModelId;
                    AgentConfig.Config.Model = model.ModelId;
                    AgentConfig.Config.HostUrl = model.HostUrl;
                    AgentConfig.Config.Stream = model.Stream;
                    _appendText($"[OK] Now using: {model.DisplayName ?? model.ModelId}", false);
                    _updateStatus();
                    break;

                default:
                    _appendText("Usage: /models list | use <id>", false);
                    break;
            }
        }
    }
}
