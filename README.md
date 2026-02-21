# T.H.U.V.U. — Tool for Heuristic Universal Versatile Usage
<img src="images/thuvu.png" width="300" alt="T.H.U.V.U. Logo">

A **local-first AI coding agent** that performs software engineering tasks autonomously using local or cloud LLMs. It prioritizes privacy, autonomy, extensibility, and safety.

## Why this exists
I vibe-coded this agent using mainly ChatGPT and GitHub Copilot in order to better understand the mechanics of AI agents and see 
how far can I go by using local LLMs. I did this because I was disappointed by the current state of the cli tools
that use local LLMs and I wanted to create a simple agent that can use tools and chat with the user. Obviously,
the inspiration for this project is Claude Code and Gemini CLI. But I want to be able to run it locally,
without the need for an API key or internet connection. I also wanted to see how far I can go with a local LLM.

## Quick Start

### Prerequisites
- .NET 8.0 SDK
- Local LLM server (LM Studio) OR cloud API (DeepSeek, OpenAI-compatible)
- Optional: Docker for RAG database, Deno for MCP code execution

### Running THUVU

```bash
# Build the project
dotnet build

# Run Desktop app (Avalonia UI — recommended)
cd thuvu.Desktop && dotnet run

# Run with TUI (terminal multi-panel interface)
dotnet run -- --tui

# Run with Web UI
dotnet run -- --web
# Then open http://localhost:5000

# Run in CLI mode
dotnet run
```

### Docker Deployment

```bash
cd docker

# Full stack with local Ollama LLM
docker-compose --profile full up -d

# With external LLM (edit .env first)
docker-compose up -d thuvu postgres-rag

# Pull models for Ollama (first time)
docker-compose --profile setup up
```

See [docker/README.md](docker/README.md) for detailed Docker deployment instructions.

## Supported Models and Providers
THUVU works with any OpenAI-compatible API:

**Local LLMs (via LM Studio or Ollama):**
- qwen/qwen3-coder-30b (recommended for coding tasks)
- qwen2.5-coder:14b (good balance of speed and capability)
- mistralai/devstral-small (fast, good for simpler tasks)
- Any model with tool/function calling support

**Cloud Providers:**
- DeepSeek API (deepseek-chat, deepseek-reasoner)
- OpenAI API
- Any OpenAI-compatible API endpoint

**LM Studio Setup:**
- LM Studio 0.3.23+ recommended
- Look for models with the hammer icon (tool calling support)
- Increase context window for complex tasks (increases memory usage)

<img src="images/lmstudio_model_settings.png" width="600" alt="LM Studio Model Settings">

## Features

### Core Features
- **LLM Integration**: Connect to local LLMs via LM Studio/Ollama, or cloud APIs (DeepSeek, OpenRouter, OpenAI)
- **Multi-Model Support**: Configure multiple models with automatic selection based on task type
- **Model-Specific Prompts**: Each model can have its own system prompt template
- **Tool System**: 30+ tools for file operations, dotnet, git, NuGet, browser automation, LSP, and more
- **Permission System**: Granular permission control with persistence and Web UI approval dialog
- **Four Interfaces**: CLI, TUI (Terminal.GUI), Web (Blazor Server), and Desktop (Avalonia)
- **Context Management**: Automatic summarization when context is near limit, token tracking
- **Tool Result Compression**: Automatic compression of large tool outputs to save tokens
- **LSP Code Intelligence**: OmniSharp-powered go-to-definition, find-references, hover, and auto-diagnostics for C#

### Desktop Application (Avalonia UI)
Cross-platform desktop client with a modern multi-panel interface:
- **Multi-Chat Tabs**: Run multiple conversations simultaneously
- **Agents Panel**: Manage in-process and detached agent instances
- **File Tree Browser**: Navigate project files with integrated editor
- **Terminal View**: Embedded terminal output
- **Diff View**: Visual git diff display
- **Model & Prompt Selection**: Dropdown selectors for switching models and prompts per-chat
- **Detached Agents**: Spawn agents as separate processes that survive Desktop restarts
  - Model/prompt overrides passed to detached agents
  - Auto-reconnection to running agents on startup
  - Full message persistence for chat history restore

```bash
# Run the Desktop app
cd thuvu.Desktop && dotnet run
```

### Web Interface
Modern Blazor Server web UI with real-time SignalR streaming:
- **Chat Interface**: Send messages and receive streaming responses
- **Workspace Browser**: Navigate and preview files with refresh button
- **Settings Panel**: Configure providers, agent settings, and features
- **Permission Dialog**: Approve or deny tool calls with Always/Session/Once/Deny options
- **Slash Commands**: Full support for all THUVU commands with autocomplete
- **File References**: Use `@filename` to include file contents in your message
- **Orchestration Panel**: Visual multi-agent task execution with per-task output
- **Screenshot Display**: View browser automation screenshots inline
- **Auto-Summarization**: Automatic context compression at 90% usage

<img src="images/thuvu-web.png" width="800" alt="T.H.U.V.U. Web Interface">

### TUI Interface
Terminal.GUI-based multi-panel interface:
- **Multi-panel Layout**: Orchestrator output, agent tabs, and input area
- **Command Autocomplete**: Tab key for commands and file paths
- **Real-time Progress**: Tool execution with elapsed time display
- **Keyboard Navigation**: ESC to cancel, arrow keys for autocomplete

### Browser Automation
Playwright-based web browsing tools for research and testing:

```bash
# Install browsers (first time)
/browser install

# Navigate and interact
/browser open https://example.com
/browser click "button#submit"
/browser type "#search" "query text"
/browser screenshot

# Get page elements
/browser elements "a.nav-link"

# Execute JavaScript
/browser script "document.title"

# Close browser
/browser close
```

### Multi-Agent Orchestration
Decompose complex tasks and run multiple agents in parallel:

```bash
# Create a task decomposition plan
/plan Create an ASP.NET Core web app with authentication and database

# Run orchestration with multiple agents
/orchestrate --agents 3

# Resume after interruption
/orchestrate --retry

# Use TUI mode for visual progress
/orchestrate --tui
```

### System Prompts
Each model can have its own system prompt for optimized behavior:

```bash
# List available prompt templates
/prompt list

# Apply a template to current session
/prompt use coding

# Show current system prompt
/prompt show

# Reload prompt from model configuration
/prompt reload
```

Configure in `appsettings.json`:
```json
{
  "Models": {
    "Models": [
      {
        "ModelId": "deepseek-chat",
        "SystemPromptTemplate": "deepseek"
      },
      {
        "ModelId": "custom-model",
        "SystemPrompt": "@prompts/my-custom-prompt.md"
      }
    ]
  }
}
```

See [prompts/README.md](prompts/README.md) for template documentation.

### RAG (Retrieval-Augmented Generation)
Semantic search across your codebase using PostgreSQL with pgvector:

```bash
# Start RAG database
cd docker && docker-compose up -d postgres-rag

# Enable and index
/rag enable
/rag index src/ --recursive --pattern *.cs

# Search semantically
/rag search "how to handle HTTP requests"
```

### Code Indexing
Local SQLite-based code indexing for fast symbol search across multiple languages. The database is created per-project in the work directory.

**Supported Languages:**
- C# (.cs) - Full Roslyn-based parsing with accurate symbol extraction
- TypeScript/JavaScript (.ts, .tsx, .js, .jsx) - Regex-based parsing
- Python (.py, .pyw, .pyi) - Regex-based parsing
- Go (.go) - Regex-based parsing

**Symbol Types Extracted:**
- Classes, interfaces, structs, enums
- Methods, functions, constructors
- Properties, fields, constants
- Namespaces, modules

```bash
# Index the current project (recursive by default)
code_index({"path": "."})

# Index specific directory with file pattern
code_index({"path": "src/", "pattern": "*.cs"})

# Search for symbols by name
code_query({"search": "Controller"})

# Search by symbol kind
code_query({"search": "User", "kind": "class"})

# Get all symbols in a file
code_query({"file": "src/Services/UserService.cs"})

# Full-text search in symbol names
code_query({"search": "Handle", "kind": "method"})

# Get index statistics
index_stats({})

# Clear all indexed data
index_clear({})
```

**Context Storage:**
Store and retrieve context/decisions for later use:

```bash
# Store architectural decisions
context_store({"key": "api_pattern", "value": "Use REST with JSON responses", "category": "decision"})

# Store technical notes
context_store({"key": "auth_flow", "value": "JWT tokens with refresh", "category": "architecture"})

# Retrieve by key
context_get({"key": "api_pattern"})

# Retrieve all in a category
context_get({"category": "decision"})

# List all stored context
context_get({})
```

See [docs/code-indexing.md](docs/code-indexing.md) for full documentation.

### LSP Code Intelligence
Language Server Protocol integration for IDE-quality code navigation and real-time diagnostics. Currently supports C# via OmniSharp, with architecture ready for TypeScript, Python, and Go.

**Operations available via the `lsp` tool:**
- `goToDefinition` — Find where a symbol is defined (cross-file, cross-project)
- `findReferences` — Find all usages of a symbol (type-aware)
- `goToImplementation` — Find implementations of interfaces/abstract methods
- `hover` — Get type info and documentation
- `documentSymbol` — List all symbols in a file
- `workspaceSymbol` — Search symbols across the project
- `prepareCallHierarchy` / `incomingCalls` / `outgoingCalls` — Call hierarchy navigation
- `diagnostics` — Get compiler errors/warnings for a file

**Auto-Diagnostics**: After every file write or patch, the agent automatically checks for compiler errors via LSP — catching mistakes instantly without running `dotnet build`.

**OmniSharp Setup**: OmniSharp auto-downloads on first use. Override in config:
```json
{
  "LspConfig": {
    "Enabled": true,
    "AutoDiagnostics": true,
    "DiagnosticsTimeoutMs": 3000,
    "Servers": {
      "omnisharp": {
        "Path": "",
        "AutoDownload": true,
        "Extensions": [".cs", ".csx"]
      }
    }
  }
}
```

**Slash Commands:**
```bash
/lsp status         # Show LSP server status
/lsp restart        # Restart all LSP servers
/lsp diagnostics <file>  # Show diagnostics for a file
```

### Multi-Agent Communication API
Run THUVU as an API server that accepts jobs from other agents. This enables distributed task execution across multiple specialized agents.

**Starting Agent API Mode:**

```bash
# Start agent with API enabled on default port (5001)
dotnet run -- --api

# Start with custom port
dotnet run -- --api --port 5002

# Start with custom configuration file
dotnet run -- --api --config agent2.json --port 5002

# Combine with web UI
dotnet run -- --web --api --port 5001
```

**Agent Dashboard:**
Access `http://localhost:5001/agent` to view:
- Current job status and real-time journal updates
- Recent job history (last 50 jobs)
- Agent configuration and capabilities

**API Endpoints:**

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/api/agent/info` | GET | Get agent name, status, and capabilities |
| `/api/jobs` | POST | Submit a new job (returns job ID) |
| `/api/jobs/current` | GET | Get current job status and journal |
| `/api/jobs/{id}` | GET | Get specific job by ID |
| `/api/jobs/{id}` | DELETE | Cancel a running job |

**Job Lifecycle:**
1. **Submit**: POST `/api/jobs` with `{"prompt": "your task"}` - returns job ID immediately
2. **Monitor**: GET `/api/jobs/current` to check status and read journal entries
3. **Result**: GET `/api/jobs/{id}` when status is "completed" to get the result

Job states: `pending` → `running` → `completed` | `failed` | `cancelled`

**Agent-to-Agent Tools:**
When an agent needs to delegate work to another agent:

```bash
# List known agents
agent_list({})

# Submit a job to another agent
agent_submit({"agent": "Agent-2", "prompt": "Analyze the authentication module"})

# Check job status and read journal
agent_status({"agent": "Agent-2", "job_id": "abc123"})

# Get completed result
agent_result({"agent": "Agent-2", "job_id": "abc123"})

# Cancel a running job
agent_cancel({"agent": "Agent-2", "job_id": "abc123"})
```

**Configuration:**
Add `AgentApiConfig` section to `appsettings.json`:

```json
{
  "AgentApiConfig": {
    "Enabled": false,
    "Port": 5001,
    "AgentName": "Agent-1",
    "AgentDescription": "Primary development agent",
    "UseHttps": false,
    "BearerToken": "optional-secret-token",
    "MaxJobHistory": 50,
    "KnownAgents": [
      {
        "Name": "Agent-2",
        "Url": "http://localhost:5002",
        "BearerToken": ""
      },
      {
        "Name": "Agent-3",
        "Url": "http://localhost:5003",
        "BearerToken": ""
      }
    ]
  }
}
```

**Multi-Agent Setup Example:**

```bash
# Terminal 1: Start primary agent
dotnet run -- --api --port 5001 --config agent1.json

# Terminal 2: Start secondary agent
dotnet run -- --api --port 5002 --config agent2.json

# Terminal 3: Start orchestrator agent
dotnet run -- --api --port 5003 --config orchestrator.json
```

Each agent can have its own:
- Configuration file with different models
- Work directory for isolation
- Known agents list for communication
- Bearer token for authentication

### MCP Code Execution
Execute TypeScript in a secure Deno sandbox with access to all tools:

```bash
# Check and enable MCP
/mcp check
/mcp enable

# Run TypeScript code
/mcp run "const files = await searchFiles('**/*.cs'); return files.length;"
```

## CLI Flags Reference

| Flag | Description |
|------|-------------|
| `--tui` | Start with Terminal UI (multi-panel interface) |
| `--web` | Start web server with Blazor UI at http://localhost:5000 |
| `--api` | Enable Agent API server for multi-agent communication |
| `--port <number>` | Override API server port (default: 5001) |
| `--config <path>` | Use custom configuration file |
| `--test-sqlite` | Run SQLite code indexing integration tests |
| `--test-ui` | Run UI automation tests |
| `--test-process` | Run process management tests |

## Commands Reference

| Command | Description |
|---------|-------------|
| `/help` | Show available commands |
| `/exit` | Quit the agent |
| `/clear` | Reset conversation |
| `/system <text>` | Set system prompt |
| `/prompt [list\|use\|show\|reload]` | Manage system prompt templates |
| `/stream on\|off` | Toggle streaming |
| `/diff [options]` | Show git diff |
| `/test [options]` | Run dotnet tests |
| `/run CMD [args]` | Run whitelisted command |
| `/commit "msg"` | Commit with test gate |
| `/push [options]` | Safe push with checks |
| `/pull [options]` | Safe pull with autostash |
| `/config` | View/manage configuration |
| `/set key value` | Change settings |
| `/rag <subcommand>` | RAG operations (index, search, stats, clear) |
| `/mcp <subcommand>` | MCP code execution |
| `/browser <subcommand>` | Browser automation |
| `/plan <task>` | Decompose task into subtasks |
| `/orchestrate [options]` | Run multi-agent orchestration |
| `/models [list\|use]` | List and switch models |
| `/lsp [status\|restart\|diagnostics]` | LSP server management |
| `/summarize` | Summarize conversation to reduce context |
| `/health` | Check service health |

## Available Tools

### File & Code Tools
| Tool | Description | Risk Level |
|------|-------------|------------|
| `search_files` | Glob search with optional content query | ReadOnly |
| `read_file` | Read file contents with SHA256 hash | ReadOnly |
| `write_file` | Write file with checksum validation | Write |
| `apply_patch` | Apply unified diff patches | Write |
| `run_process` | Execute whitelisted commands | Write |
| `execute_code` | Run TypeScript in Deno sandbox | Write |

### .NET Tools
| Tool | Description | Risk Level |
|------|-------------|------------|
| `dotnet_restore` | NuGet restore | Write |
| `dotnet_build` | Build solution/project | Write |
| `dotnet_test` | Run tests | Write |
| `dotnet_run` | Run application (blocking with timeout) | Write |
| `dotnet_new` | Create new project | Write |

### Git & Package Tools
| Tool | Description | Risk Level |
|------|-------------|------------|
| `git_status` | Branch and working tree status | ReadOnly |
| `git_diff` | Show file diffs | ReadOnly |
| `nuget_search` | Search packages | ReadOnly |
| `nuget_add` | Add package to project | Write |

### RAG Tools
| Tool | Description | Risk Level |
|------|-------------|------------|
| `rag_index` | Index files for semantic search | Write |
| `rag_search` | Query indexed content | ReadOnly |
| `rag_stats` | Index statistics | ReadOnly |
| `rag_clear` | Clear index | Write |

### Browser Automation Tools
| Tool | Description | Risk Level |
|------|-------------|------------|
| `browser_navigate` | Navigate to URL | Write |
| `browser_click` | Click element | Write |
| `browser_type` | Type text into element | Write |
| `browser_get_elements` | Query page elements | ReadOnly |
| `browser_screenshot` | Capture screenshot | ReadOnly |
| `browser_script` | Execute JavaScript | Write |
| `browser_close` | Close browser | Write |

### Process Management Tools
| Tool | Description | Risk Level |
|------|-------------|------------|
| `process_start` | Start background process (non-blocking) | Write |
| `process_read` | Read process stdout/stderr | ReadOnly |
| `process_write` | Write to process stdin | Write |
| `process_status` | Get process status or list sessions | ReadOnly |
| `process_stop` | Stop a background process | Write |

### UI Automation Tools
| Tool | Description | Risk Level |
|------|-------------|------------|
| `ui_capture` | Screenshot (screen/window/region) with optional vision analysis | UIAutomation |
| `ui_list_windows` | List open windows | UIAutomation |
| `ui_focus_window` | Focus/activate a window | UIAutomation |
| `ui_click` | Mouse click at coordinates | UIAutomation |
| `ui_type` | Keyboard input (text/shortcuts) | UIAutomation |
| `ui_mouse_move` | Move cursor | UIAutomation |
| `ui_get_element` | Get UI element at point or by selector | UIAutomation |
| `ui_wait` | Wait for window or element | UIAutomation |

### Code Indexing Tools
| Tool | Description | Risk Level |
|------|-------------|------------|
| `code_index` | Index source code for symbol search (C#, TS, Python, Go) | Write |
| `code_query` | Search indexed symbols (classes, methods, etc.) | ReadOnly |
| `context_store` | Store context/memory for later retrieval | Write |
| `context_get` | Retrieve stored context by key/category | ReadOnly |
| `index_stats` | Get code index statistics | ReadOnly |
| `index_clear` | Clear all indexed data | Write |

### LSP Code Intelligence Tools
| Tool | Description | Risk Level |
|------|-------------|------------|
| `lsp` | LSP operations: goToDefinition, findReferences, hover, documentSymbol, workspaceSymbol, goToImplementation, callHierarchy, diagnostics | ReadOnly |

### Agent Communication Tools
| Tool | Description | Risk Level |
|------|-------------|------------|
| `agent_list` | List known agents and their status | AgentCommunication |
| `agent_submit` | Submit a job to another agent | AgentCommunication |
| `agent_status` | Get job status and journal from another agent | AgentCommunication |
| `agent_result` | Get completed job result from another agent | AgentCommunication |
| `agent_cancel` | Cancel a running job on another agent | AgentCommunication |

## Configuration

See [docs/configuration.md](docs/configuration.md) for detailed configuration options.

Basic `appsettings.json`:

```json
{
  "AgentConfig": {
    "HostUrl": "http://127.0.0.1:1234",
    "Model": "qwen/qwen3-coder-30b",
    "Stream": true,
    "TimeoutMs": 1800000,
    "WorkDirectory": "./work",
    "MaxContextLength": 130000,
    "MaxIterations": 50
  },
  "Models": {
    "DefaultModelId": "qwen/qwen3-coder-30b",
    "Models": [
      {
        "ModelId": "qwen/qwen3-coder-30b",
        "DisplayName": "Qwen3 Coder 30B",
        "HostUrl": "http://127.0.0.1:1234",
        "IsLocal": true,
        "SupportsTools": true,
        "MaxContextLength": 130000,
        "SystemPromptTemplate": "coding"
      }
    ]
  },
  "RagConfig": {
    "ConnectionString": "Host=localhost;Port=5433;Database=thuvu_rag;...",
    "Enabled": true
  },
  "McpConfig": {
    "Enabled": true,
    "DenoPath": "deno"
  }
}
```

## Architecture

```
thuvu.sln
├── thuvu/                      # CLI + Agent Core (entry point)
│   ├── Program.cs              # Entry point, command routing
│   ├── AgentLoop.cs            # LLM conversation loop with tool calling
│   ├── ToolExecutor.cs         # Tool dispatch and execution
│   ├── TuiInterface.cs         # Terminal.GUI interface
│   │
│   ├── Models/                 # Data models and core logic
│   │   ├── AgentConfig.cs          # Main configuration
│   │   ├── ModelConfig.cs          # Multi-model registry
│   │   ├── LspConfig.cs            # LSP server configuration
│   │   ├── SystemPromptManager.cs  # Model-specific prompts
│   │   ├── TaskDecomposition.cs    # Task planning
│   │   ├── TaskOrchestrator.cs     # Multi-agent coordination
│   │   ├── PermissionManager.cs    # Security permissions
│   │   ├── TokenTracker.cs         # Context tracking
│   │   └── McpCodeExecutor.cs      # Deno sandbox
│   │
│   ├── Services/
│   │   └── Lsp/                    # LSP Code Intelligence
│   │       ├── ILspServer.cs       # Language server abstraction
│   │       ├── LspService.cs       # Server lifecycle manager
│   │       ├── LspClient.cs        # JSON-RPC client (StreamJsonRpc)
│   │       ├── OmniSharpServer.cs  # C# OmniSharp implementation
│   │       └── LspDownloadService.cs # Auto-download binaries
│   │
│   ├── Tools/                  # Tool implementations (30+)
│   │   ├── LspToolImpl.cs         # LSP code intelligence tool
│   │   ├── BrowserToolImpl.cs     # Playwright browser
│   │   ├── ReadFileToolImpl.cs    # File read with hash
│   │   ├── WriteFileToolImpl.cs   # File write + auto-diagnostics
│   │   ├── ApplyPatchToolImpl.cs  # Diff patch + auto-diagnostics
│   │   └── ...
│   │
│   ├── Web/                    # Blazor Server Web Interface
│   │   ├── WebHost.cs              # ASP.NET Core host
│   │   ├── AgentApiEndpoints.cs    # Agent REST API
│   │   ├── Hubs/AgentHub.cs       # SignalR hub
│   │   └── Components/            # Blazor components
│   │
│   ├── prompts/                # System prompt templates
│   ├── mcp/                    # MCP TypeScript ecosystem
│   ├── docker/                 # Docker deployment
│   └── docs/                   # Documentation
│
├── thuvu.Core/                 # Shared library
│   ├── Services/
│   │   └── IAgentService.cs        # Agent abstraction interface
│   ├── Models/                     # Shared data models
│   └── AgentLoop.cs                # Core agent execution
│
└── thuvu.Desktop/              # Avalonia UI Desktop Application
    ├── Views/                  # XAML views
    │   ├── ChatView.axaml          # Main chat interface
    │   ├── EditorView.axaml        # Code editor
    │   ├── AgentsPanelView.axaml   # Agent management
    │   ├── FileTreeView.axaml      # File browser
    │   ├── TerminalView.axaml      # Terminal output
    │   └── DiffView.axaml          # Git diff display
    ├── ViewModels/             # MVVM view models
    ├── Services/
    │   ├── DesktopAgentService.cs  # In-process agent
    │   ├── RemoteAgentService.cs   # HTTP+SSE detached agent
    │   ├── AgentRegistry.cs        # Agent discovery
    │   └── AgentProcessManager.cs  # Process lifecycle
    ├── Controls/               # Custom Avalonia controls
    └── App.axaml.cs            # Application entry
```

## Documentation

- [Configuration Guide](docs/configuration.md) - Detailed configuration options
- [Docker Deployment](docker/README.md) - Multi-container Docker setup
- [System Prompts](prompts/README.md) - Custom prompt templates
- [Multi-Agent Orchestration](docs/orchestration.md) - Task decomposition and parallel execution
- [Code Indexing](docs/code-indexing.md) - SQLite-based symbol search
- [UI Automation](docs/ui-automation-plan.md) - Screen capture and input control
- [Agent Communication API](docs/orchestration.md#agent-api) - REST API for multi-agent setups

## Why the name THUVU?
The name is a reference to the late and great Greek comedian Thanassis Veggos who made a 2-part film series 
where the main character (ΘΒ) Θου Βου (Thou Vou) was an aspiring secret agent, studying at the
secret agent school and messing up all the tasks he was assigned.

## Next Steps
- **Snapshot/Rollback**: Git-based checkpoints before risky operations for safe undo
- **Batch/Parallel Tools**: Execute multiple independent tool calls simultaneously
- **LLM-Managed Todo**: Agent maintains a structured todo list for complex tasks
- **Plan-Only Mode**: Read-only agent for analysis without code changes
- **Lightweight Web Fetch**: Simple HTTP GET tool for docs/APIs (complement to full Playwright browser)
- **Git Worktrees**: Agent-per-worktree isolation for parallel tasks
- **Multi-Language LSP**: Add TypeScript (tsserver), Python (pylsp), Go (gopls)
- **Plugin System**: Discover and install community tool packs

## Recent Changes

### LSP Code Intelligence (NEW)
Language Server Protocol integration for IDE-quality code navigation:

- **OmniSharp for C#**: Go-to-definition, find-references, hover, call hierarchy
- **Auto-Diagnostics**: Compiler errors checked after every file write/patch
- **Auto-Download**: OmniSharp downloads automatically on first use
- **Multi-Language Ready**: `ILspServer` abstraction for future language servers
- **10 LSP Operations**: Full tool (`lsp`) with definition, references, implementation, symbols, diagnostics, and call hierarchy

### Desktop Application (NEW)
Avalonia-based desktop client with rich multi-panel UI:

- **Multi-Chat Interface**: Tabbed conversations with independent agent sessions
- **Dual-Mode Agents**: In-process (fast) and detached (survives restarts) modes
- **Agent Panel**: Manage, monitor, and terminate agent instances
- **File Tree & Editor**: Project navigation and code viewing
- **SSE Streaming**: Real-time token streaming from detached agents
- **Message Persistence**: SQLite-backed chat history for detached agents

### Multi-Agent Communication API
Run THUVU as an API server that accepts jobs from other agents:

- **Agent API Mode**: Start with `--api` flag to enable REST API server
- **Job Management**: Submit jobs, monitor progress via journal, get results
- **Agent Dashboard**: Web UI at `/agent` showing current job, journal, and history
- **Inter-Agent Tools**: `agent_list`, `agent_submit`, `agent_status`, `agent_result`, `agent_cancel`
- **SQLite Persistence**: Jobs persist across restarts with full journal history
- **Authentication**: Optional Bearer token authentication for secure communication
- **CLI Flags**: `--api`, `--port`, `--config` for flexible multi-agent setups

**Quick Start:**
```bash
# Start agent with API
dotnet run -- --api --port 5001

# Access dashboard at http://localhost:5001/agent
```

### SQLite Code Indexing
Local code indexing for fast symbol search:

- **Multi-Language Support**: C# (Roslyn), TypeScript, Python, Go (regex-based)
- **Symbol Extraction**: Classes, methods, properties, functions, interfaces
- **Per-Project Database**: SQLite database created in work directory
- **Context Storage**: Store and retrieve architectural decisions and notes
- **Incremental Indexing**: Skip unchanged files based on content hash

**Tools Added:**
- `code_index` - Index source files
- `code_query` - Search symbols by name, kind, or file
- `context_store` / `context_get` - Store and retrieve context
- `index_stats` / `index_clear` - Manage index

### Vision/Image Analysis
Added support for analyzing images using vision-capable LLMs:

- **Image Paste/Drop**: Paste images (Ctrl+V) or drag-drop into the Web UI chat input
- **Vision Model Configuration**: Configure a vision model in `appsettings.json`:
  ```json
  {
    "Models": {
      "VisionModelId": "qwen3-vl-8b",
      "Models": [
        {
          "ModelId": "qwen3-vl-8b",
          "DisplayName": "Qwen3 VL 8B",
          "HostUrl": "http://127.0.0.1:1234",
          "SupportsVision": true,
          "Purposes": ["Vision"]
        }
      ]
    }
  }
  ```
- **Automatic Image Resizing**: Large images are automatically resized to prevent vision model errors
- **Context Integration**: Vision analysis results are added to conversation history for follow-up questions

New tool: `analyze_image` - Analyze images via vision-capable LLM

### MCP/Execute Code Fixes
- **Deno Permission Fix**: Fixed `--allow-read` to include both work directory and MCP directory
- **MCP Context for Permissions**: Auto-grant permissions for nested tool calls within MCP sandbox
- **Console.log Capture**: Fixed console.log output capture in sandbox
- `Tools/CodeIndexer.cs` (new) - Multi-language code parser
- `Tools/Parsers/` (new) - Language-specific parsers (Python, TypeScript, Go)
- `Program.cs` - Added --api, --port, --config CLI flags
- `Web/WebHost.cs` - Agent API endpoint mapping
- `Models/PermissionManager.cs` - Added AgentCommunication permission category
- `Tools/BuildTools.cs` - Added agent communication and code indexing tool definitions
- `ToolExecutor.cs` - Added dispatch logic for new tools

## Performance
Tested on ThinkPad L14 with Ryzen 5 Pro 4650U and 64GB RAM running Windows 11. Works well with local LLMs running mainly on CPU, though cloud APIs (DeepSeek) provide faster responses for complex tasks.
