# T.H.U.V.U. — Tool for Heuristic Universal Versatile Usage
<img src="images/thuvu.png" width="300" alt="T.H.U.V.U. Logo">

A **local-first AI coding agent** that performs software engineering tasks autonomously using local or cloud LLMs. It prioritizes privacy, autonomy, extensibility, and safety.

## Why this exists
I vibe-coded this agent using mainly ChatGPT and GitHub Copilot in order to better understand the mechanics of AI agents and see 
how far can I go by using local LLMs. I did this because I was disappointed by the current state of the cli tools
that use local LLMs and I wanted to create a simple agent that can use tools and chat with the user. Obviously,
the inspiration for this project is Claude Code and Gemini CLI. But I want to be able to run it locally,
without the need for an API key or internet connection. I also wanted to see how far I can go with a local LLM.

## Supported Models and Providers
THUVU works with any OpenAI-compatible API:

**Local LLMs (via LM Studio):**
- qwen/qwen3-coder-30b (recommended for coding tasks)
- qwen/qwen3-4b-2507 (fast, runs on CPU)
- ibm/granite-4-h-tiny
- Any model with tool/function calling support (indicated by hammer icon in LM Studio)

**Cloud Providers:**
- DeepSeek API (deepseek-chat, deepseek-reasoner)
- Any OpenAI-compatible API endpoint

**LM Studio Setup:**
- LM Studio 0.3.23+ recommended
- You can increase the context window to the maximum supported by the model (increases memory usage)
- For qwen3-4b-2507, memory usage can reach 38 GB with max context
- If low on VRAM, uncheck "Offload KV cache to GPU memory" in model settings

<img src="images/lmstudio_model_settings.png" width="600" alt="LM Studio Model Settings">

## How to run
1. Download the code and extract it to a folder. Build with Visual Studio 2022+, VS Code with C# extension, or JetBrains Rider (.NET 8.0 required).
2. Start LM Studio and load a model on the Developer tab (served on http://localhost:1234 by default), OR configure a cloud API in `appsettings.json`.
3. Run `thuvu.exe` - the TUI interface will start with health checks. Type messages or use `/help` for commands.

**TUI Interface Features:**
- Multi-panel layout with orchestrator output, agent tabs, and input area
- Command autocomplete (Tab key)
- File/directory autocomplete with `@` prefix
- Real-time tool execution progress with elapsed time
- ESC key to cancel running operations

## How to test
You can test the agent by requesting it to create projects. Example tasks:
- "Create a Fibonacci calculation program"
- "Create an ASP.NET Core web app with authentication"
- Use `/plan` to decompose complex tasks and `/orchestrate` to run with multiple agents

## Why the name thuvu?
The name is a reference to the late and great Greek comedian Thanassis Veggos who made a 2 part film series 
where the main character (ΘΒ) Θου Βου (Thou Vou) was an aspiring secret agent, studying at the
secret agent school and messing up all the tasks he was assigned.

## Features

### Core Features
- **LLM Integration**: Connect to local LLMs via LM Studio's OpenAI-compatible REST API, or cloud APIs (DeepSeek, etc.)
- **Multi-Model Support**: Configure multiple models with automatic selection based on task type (thinking vs coding)
- **Tool System**: 20+ tools for file operations, dotnet commands, git operations, NuGet, and process execution
- **Permission System**: Granular permission control with persistence (always allow/deny per tool)
- **TUI Interface**: Terminal.GUI-based multi-panel interface with agent tabs and progress tracking
- **Context Management**: Automatic summarization when context is near limit, token tracking

### Multi-Agent Orchestration
Decompose complex tasks and run multiple agents in parallel:

```bash
# Create a task decomposition plan
/plan Create an ASP.NET Core web app with authentication and database

# Run orchestration with multiple agents
/orchestrate --agents 3

# Resume after interruption (resets stuck tasks)
/orchestrate --retry

# Use TUI mode for visual progress
/orchestrate --tui

# Start fresh
/orchestrate --reset
```

**Orchestration Features:**
- Task decomposition with dependency analysis
- Parallel agent execution (configurable agent count)
- Progress tracking with file-based persistence (`current-plan.json`)
- Automatic retry of failed/interrupted tasks
- Git integration for change tracking
- Thinking model escalation for complex tasks

### RAG (Retrieval-Augmented Generation)
The agent supports RAG for semantic search across your codebase using PostgreSQL with pgvector:

```bash
# Enable RAG (requires PostgreSQL with pgvector)
/rag enable

# Index your source code
/rag index src/ --recursive --pattern *.cs

# Search semantically
/rag search "how to handle HTTP requests"

# View stats
/rag stats

# Clear index
/rag clear
```

**RAG Setup Requirements:**

**Option 1: Using Docker (Recommended)**
```bash
cd docker
docker-compose up -d
```
This starts PostgreSQL 16 with pgvector pre-installed. Connection details:
- Host: `localhost`
- Port: `5432`
- Database: `thuvu_rag`
- User: `thuvu`
- Password: `thuvu_secret`

**Option 2: Manual Setup**
1. PostgreSQL 15+ with pgvector extension installed
2. Create a database: `CREATE DATABASE thuvu_rag;`
3. Run the schema: `docker/init/01-init-rag.sql`

Configure connection in `appsettings.json`:

```json
{
  "RagConfig": {
    "ConnectionString": "Host=localhost;Port=5433;Database=thuvu_rag;Username=thuvu;Password=thuvu_secret",
    "EmbeddingDimension": 1536,
    "EmbeddingHostUrl": "http://127.0.0.1:1234",
    "EmbeddingModel": "text-embedding-nomic-embed-text-v1.5",
    "Enabled": true
  }
}
```

### Logging
Structured logging with session tracking:
- Per-agent log files in `work/logs/` directory
- Session-based log separation for orchestration
- Tool execution timing and progress tracking

## Commands Reference

| Command | Description |
|---------|-------------|
| `/help` | Show available commands |
| `/exit` | Quit the agent |
| `/clear` | Reset conversation |
| `/system <text>` | Set system prompt |
| `/stream on\|off` | Toggle streaming |
| `/diff [options]` | Show git diff |
| `/test [options]` | Run dotnet tests |
| `/run CMD [args]` | Run whitelisted command |
| `/commit "msg"` | Commit with test gate |
| `/push [options]` | Safe push with checks |
| `/pull [options]` | Safe pull with autostash |
| `/config` | View/manage configuration |
| `/set key value` | Change settings |
| `/rag subcommand` | RAG operations (index, search, stats, clear) |
| `/mcp subcommand` | MCP code execution |
| `/plan <task>` | Decompose task into subtasks |
| `/orchestrate` | Run multi-agent orchestration |
| `/models` | List and switch models |
| `/summarize` | Summarize conversation to reduce context |

### MCP (Model Context Protocol) Code Execution

Execute TypeScript code in a secure Deno sandbox with access to all THUVU tools. Based on [Anthropic's MCP paper](https://www.anthropic.com/engineering/code-execution-with-mcp).

**Requirements:** [Deno](https://deno.land) runtime installed.

```bash
# Check MCP environment
/mcp check

# Enable MCP
/mcp enable

# Run TypeScript code
/mcp run "const files = await searchFiles('**/*.cs'); return files.length;"

# List available tools
/mcp tools

# View configuration
/mcp config
```

**Benefits:**
- Execute multiple tool calls in a single request
- Process data locally in the sandbox
- Return only relevant results (token reduction)
- Full TypeScript/JavaScript capabilities

## Configuration

Example `appsettings.json`:

```json
{
  "AgentConfig": {
    "HostUrl": "http://127.0.0.1:1234",
    "Model": "qwen/qwen3-coder-30b",
    "Stream": true,
    "TimeoutMs": 1800000,
    "WorkDirectory": "./work",
    "MaxContextLength": 65536
  },
  "Models": {
    "DefaultModelId": "deepseek-chat",
    "ThinkingModelId": "deepseek-reasoner",
    "AutoSelectModel": true,
    "Models": [
      {
        "ModelId": "qwen/qwen3-coder-30b",
        "DisplayName": "Qwen3 Coder 30B",
        "HostUrl": "http://127.0.0.1:1234",
        "IsLocal": true,
        "SupportsTools": true,
        "IsThinkingModel": false
      },
      {
        "ModelId": "deepseek-chat",
        "DisplayName": "DeepSeek Chat",
        "HostUrl": "https://api.deepseek.com",
        "AuthToken": "your-api-key",
        "IsLocal": false,
        "MaxContextLength": 130000,
        "MaxOutputTokens": 8096,
        "SupportsTools": true
      },
      {
        "ModelId": "deepseek-reasoner",
        "DisplayName": "DeepSeek Reasoner (Thinking)",
        "HostUrl": "https://api.deepseek.com",
        "AuthToken": "your-api-key",
        "IsLocal": false,
        "MaxContextLength": 130000,
        "MaxOutputTokens": 65536,
        "IsThinkingModel": true,
        "SupportsTools": false
      }
    ]
  }
}
```

## Available Tools

| Tool | Description | Risk Level |
|------|-------------|------------|
| `search_files` | Glob search with optional content query | ReadOnly |
| `read_file` | Read file contents with SHA256 hash | ReadOnly |
| `write_file` | Write file with checksum validation | Write |
| `apply_patch` | Apply unified diff patches | Write |
| `run_process` | Execute whitelisted commands | Write |
| `dotnet_restore` | NuGet restore | Write |
| `dotnet_build` | Build solution/project | Write |
| `dotnet_test` | Run tests | Write |
| `dotnet_run` | Run application | Write |
| `dotnet_new` | Create new project | Write |
| `git_status` | Branch and working tree status | ReadOnly |
| `git_diff` | Show file diffs | ReadOnly |
| `nuget_search` | Search packages | ReadOnly |
| `nuget_add` | Add package to project | Write |
| `rag_index` | Index files for semantic search | Write |
| `rag_search` | Query indexed content | ReadOnly |
| `rag_stats` | Index statistics | ReadOnly |
| `rag_clear` | Clear index | Write |

## Next steps
- Improve multi-agent coordination and conflict resolution
- Add more intelligent task decomposition based on codebase analysis
- Support more programming languages and frameworks beyond .NET
- Add web UI option alongside TUI
- Implement agent memory/learning across sessions
- Add support for more cloud LLM providers

## Architecture

```
thuvu/
├── Program.cs              # Entry point, command routing
├── AgentLoop.cs            # LLM conversation loop with tool calling
├── ToolExecutor.cs         # Tool dispatch and execution
├── TuiInterface.cs         # Terminal.GUI main interface
│
├── Models/                 # Data models and core logic
│   ├── TaskDecomposition.cs    # Task planning and breakdown
│   ├── TaskOrchestrator.cs     # Multi-agent coordination
│   ├── PermissionManager.cs    # Security permissions with persistence
│   ├── TokenTracker.cs         # Context length tracking
│   ├── McpCodeExecutor.cs      # Deno sandbox executor
│   └── ModelConfig.cs          # Multi-model registry
│
├── Tui/                    # TUI components
│   ├── TuiOrchestrationView.cs # Multi-agent view
│   ├── TuiMessageQueue.cs      # Thread-safe UI updates
│   └── TuiAutocomplete.cs      # Command/file autocomplete
│
├── Tools/                  # Tool implementations
│   ├── SearchFilesToolImpl.cs
│   ├── ReadFileToolImpl.cs
│   ├── WriteFileToolImpl.cs
│   └── ...
│
├── mcp/                    # MCP TypeScript ecosystem
│   ├── servers/            # Tool wrappers
│   ├── runtime/            # Sandbox execution
│   └── catalog.ts          # Tool discovery
│
└── skills/                 # Saved agent workflows
```

## Performance
Tested on ThinkPad L14 with Ryzen 5 Pro 4650U and 64GB RAM running Windows 11. Works well with local LLMs running mainly on CPU, though cloud APIs (DeepSeek) provide faster responses for complex tasks.