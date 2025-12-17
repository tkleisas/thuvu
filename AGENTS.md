# T.H.U.V.U. Project Plan
## Tool for Heuristic Universal Versatile Usage

---

## 1. Project Vision

T.H.U.V.U. is a **local-first AI coding agent** that performs software engineering tasks autonomously using local LLMs. It prioritizes:

- **Privacy**: All data stays local; no external API keys required
- **Autonomy**: Agent can understand, plan, and execute multi-step tasks
- **Extensibility**: Modular tool system with TypeScript sandbox execution
- **Safety**: Permission system and sandboxed code execution

---

## 2. Current Implementation Status

### ✅ Completed Features

| Component | Status | Description |
|-----------|--------|-------------|
| **Core Agent Loop** | ✅ Done | `AgentLoop.cs` - Streaming/non-streaming LLM interactions with tool calling |
| **LLM Integration** | ✅ Done | OpenAI-compatible REST API (LM Studio), multi-model support |
| **Tool System** | ✅ Done | 20+ tools for file ops, dotnet, git, NuGet |
| **Permission System** | ✅ Done | `PermissionManager.cs` - Granular read/write permissions |
| **RAG Support** | ✅ Done | PostgreSQL/pgvector semantic search |
| **MCP Code Execution** | ✅ Done | TypeScript sandbox via Deno (Phases 1-7) |
| **TUI Interface** | ✅ Done | Terminal.GUI-based interface |
| **Configuration** | ✅ Done | `appsettings.json` centralized config |
| **Logging** | ✅ Done | Structured logging with session tracking |
| **Skills System** | ✅ Done | Save/load reusable TypeScript workflows |

### 📁 Project Structure

```
thuvu/
├── Program.cs              # Entry point, command routing
├── AgentLoop.cs            # LLM conversation loop with tool calling
├── ToolExecutor.cs         # Tool dispatch and execution
├── ConsoleHelpers.cs       # CLI styling and output
├── TuiInterface.cs         # Terminal.GUI interface
│
├── Models/                 # Data models and configuration
│   ├── AgentConfig.cs      # Main configuration
│   ├── McpConfig.cs        # MCP sandbox settings
│   ├── RagConfig.cs        # RAG/vector search settings
│   ├── PermissionManager.cs # Security permissions
│   ├── McpBridge.cs        # C# <-> TypeScript IPC
│   ├── McpCodeExecutor.cs  # Deno sandbox executor
│   └── ModelConfig.cs      # Multi-model registry
│
├── Tools/                  # Tool implementations
│   ├── BuildTools.cs       # Tool schema definitions
│   ├── ReadFileToolImpl.cs
│   ├── WriteFileToolImpl.cs
│   ├── SearchFilesToolImpl.cs
│   ├── ApplyPatchToolImpl.cs
│   ├── RunProcessToolImpl.cs
│   ├── DotnetToolImpl.cs
│   └── RagToolImpl.cs
│
├── mcp/                    # MCP TypeScript ecosystem
│   ├── servers/            # Tool wrappers (filesystem, git, dotnet, rag)
│   ├── runtime/            # Sandbox execution (bridge.ts, sandbox.ts)
│   ├── types/              # TypeScript definitions
│   └── catalog.ts          # Tool discovery
│
├── skills/                 # Saved agent workflows
│   ├── analyze-codebase.ts
│   └── run-tests-and-fix.ts
│
├── docker/                 # PostgreSQL + pgvector setup
└── docs/                   # Documentation
```

---

## 3. Architecture

### 3.1 Agent Loop Flow

```
User Input → Command Handler → LLM Request
                                   ↓
                            Tool Detection?
                           /              \
                         Yes               No
                          ↓                 ↓
                   Tool Executor      Return Response
                          ↓
                   Permission Check
                          ↓
                   Execute Tool(s)
                          ↓
                   Append Results
                          ↓
                   Loop Back to LLM
```

### 3.2 MCP Code Execution Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                        THUVU (C#)                           │
├─────────────────────────────────────────────────────────────┤
│  AgentLoop → McpCodeExecutor → Spawn Deno Sandbox          │
│                    ↓                                        │
│              IPC Bridge (stdin/stdout JSON-RPC)             │
└─────────────────────────────────────────────────────────────┘
                           ↓
┌─────────────────────────────────────────────────────────────┐
│                    Deno Sandbox                              │
│  - TypeScript execution with restricted permissions         │
│  - Access to all THUVU tools via bridge                     │
│  - Batch operations, local data processing                  │
│  - Returns only relevant results (token reduction)          │
└─────────────────────────────────────────────────────────────┘
```

---

## 4. Available Tools

### 4.1 File System Tools
| Tool | Description | Risk Level |
|------|-------------|------------|
| `search_files` | Glob search with optional content query | ReadOnly |
| `read_file` | Read file contents with SHA256 | ReadOnly |
| `write_file` | Write file with checksum validation | Write |
| `apply_patch` | Apply unified diff patches | Write |

### 4.2 Development Tools
| Tool | Description | Risk Level |
|------|-------------|------------|
| `run_process` | Execute whitelisted commands (dotnet, git, bash, powershell) | Write |
| `dotnet_restore` | NuGet restore | Write |
| `dotnet_build` | Build solution/project | Write |
| `dotnet_test` | Run tests | Write |
| `dotnet_run` | Run application | Write |
| `dotnet_new` | Create new project | Write |

### 4.3 Git Tools
| Tool | Description | Risk Level |
|------|-------------|------------|
| `git_status` | Branch and working tree status | ReadOnly |
| `git_diff` | Show file diffs | ReadOnly |

### 4.4 NuGet Tools
| Tool | Description | Risk Level |
|------|-------------|------------|
| `nuget_search` | Search packages | ReadOnly |
| `nuget_add` | Add package to project | Write |

### 4.5 RAG Tools
| Tool | Description | Risk Level |
|------|-------------|------------|
| `rag_index` | Index files for semantic search | Write |
| `rag_search` | Query indexed content | ReadOnly |
| `rag_stats` | Index statistics | ReadOnly |
| `rag_clear` | Clear index | Write |

---

## 5. Configuration

### 5.1 appsettings.json Structure

```json
{
  "AgentConfig": {
    "HostUrl": "http://127.0.0.1:1234",
    "Model": "qwen/qwen3-coder-30b",
    "Stream": true,
    "TimeoutMs": 1800000,
    "HttpRequestTimeout": 60,
    "WorkDirectory": "./work"
  },
  "Models": {
    "DefaultModelId": "qwen/qwen3-coder-30b",
    "ThinkingModelId": "",
    "CodingModelId": "",
    "Models": [
      {
        "ModelId": "qwen/qwen3-coder-30b",
        "DisplayName": "Qwen3 Coder 30B",
        "HostUrl": "http://127.0.0.1:1234",
        "IsLocal": true,
        "SupportsTools": true,
        "Purposes": ["Default", "Coding", "Review"]
      }
    ]
  },
  "RagConfig": {
    "ConnectionString": "Host=localhost;Port=5433;Database=thuvu_rag;Username=thuvu;Password=thuvu_secret",
    "EmbeddingDimension": 1536,
    "Enabled": true,
    "EmbeddingModel": "text-embedding-nomic-embed-text-v1.5"
  },
  "McpConfig": {
    "Enabled": true,
    "DenoPath": "deno",
    "DefaultTimeout": 300000,
    "PermissionLevel": "readwrite",
    "RequireApproval": true
  }
}
```

---

## 6. Commands Reference

| Command | Description |
|---------|-------------|
| `/help` | Show available commands |
| `/exit` | Quit the agent |
| `/clear` | Reset conversation |
| `/system <text>` | Set system prompt |
| `/stream on\|off` | Toggle streaming |
| `/config` | View/manage configuration |
| `/set key value` | Change settings |
| `/diff` | Show git diff |
| `/test` | Run dotnet tests |
| `/run CMD` | Run whitelisted command |
| `/commit "msg"` | Commit with test gate |
| `/push` | Safe push with checks |
| `/pull` | Safe pull with autostash |
| `/rag <subcommand>` | RAG operations (index, search, stats, clear) |
| `/mcp <subcommand>` | MCP operations (enable, run, tools, skills) |
| `/models <subcommand>` | Model management (list, use, thinking, coding) |

---

## 7. Agent Isolation & Git Strategy

### 7.1 Overview

Each agent instance operates in **isolation** using Git branches to:
- Track all changes made by the agent
- Enable rollback if something goes wrong
- Run tests without affecting other agents
- Allow parallel agent execution on different tasks

### 7.2 Branch Naming Convention

```
agent/<agent-id>/<task-description>
```

Examples:
- `agent/thuvu-001/fix-login-bug`
- `agent/thuvu-002/add-user-validation`
- `agent/thuvu-003/refactor-database-layer`

### 7.3 Agent Workflow

```
┌─────────────────────────────────────────────────────────────┐
│                    Agent Task Lifecycle                      │
├─────────────────────────────────────────────────────────────┤
│                                                              │
│  1. INIT: Create isolated branch from main/develop          │
│     └─► git checkout -b agent/<id>/<task>                   │
│                                                              │
│  2. WORK: Make changes, commit frequently                   │
│     └─► git add . && git commit -m "step: description"      │
│                                                              │
│  3. TEST: Run tests on the branch                           │
│     └─► dotnet test (or language-specific)                  │
│                                                              │
│  4. CHECKPOINT: Tag successful milestones                   │
│     └─► git tag agent/<id>/checkpoint-N                     │
│                                                              │
│  5. ROLLBACK (if needed): Revert to last good state         │
│     └─► git reset --hard <checkpoint>                       │
│                                                              │
│  6. COMPLETE: Merge or create PR when task done             │
│     └─► git checkout main && git merge agent/<id>/<task>    │
│                                                              │
│  7. CLEANUP: Delete agent branch after merge                │
│     └─► git branch -d agent/<id>/<task>                     │
│                                                              │
└─────────────────────────────────────────────────────────────┘
```

### 7.4 Git Tools for Agents

| Tool | Description | Status |
|------|-------------|--------|
| `git_create_branch` | Create isolated agent branch | 🔲 Planned |
| `git_commit` | Commit with structured message | 🔲 Planned |
| `git_checkpoint` | Tag current state for rollback | 🔲 Planned |
| `git_rollback` | Reset to checkpoint or commit | 🔲 Planned |
| `git_merge` | Merge agent branch to target | 🔲 Planned |
| `git_cleanup` | Delete agent branch after merge | 🔲 Planned |
| `git_stash` | Stash uncommitted changes | ✅ Done (via run_process) |
| `git_status` | Check working tree status | ✅ Done |
| `git_diff` | View changes | ✅ Done |

### 7.5 Commit Message Convention

```
<type>: <description>

[optional body]

Agent: <agent-id>
Task: <task-description>
```

**Types:**
- `feat`: New feature
- `fix`: Bug fix
- `refactor`: Code restructuring
- `test`: Adding/updating tests
- `docs`: Documentation
- `chore`: Maintenance tasks
- `checkpoint`: Milestone marker

**Example:**
```
feat: add user email validation

- Added regex validation for email format
- Added unit tests for edge cases
- Updated UserService to use new validator

Agent: thuvu-001
Task: add-user-validation
```

### 7.6 Conflict Resolution Strategy

When multiple agents work on the same codebase:

1. **Prevention**: Each agent works on different files/modules when possible
2. **Detection**: Before merge, check for conflicts with `git merge --no-commit --no-ff`
3. **Resolution Options**:
   - **Auto-merge**: If changes don't overlap
   - **Rebase**: Agent rebases on latest main before merge
   - **Human review**: Flag conflicts for user resolution
   - **Abort**: Rollback agent changes if conflicts are severe

### 7.7 Agent Session State

Each agent session maintains:

```json
{
  "agentId": "thuvu-001",
  "taskDescription": "fix-login-bug",
  "branchName": "agent/thuvu-001/fix-login-bug",
  "baseBranch": "main",
  "checkpoints": [
    { "tag": "agent/thuvu-001/checkpoint-1", "commit": "abc123", "timestamp": "..." },
    { "tag": "agent/thuvu-001/checkpoint-2", "commit": "def456", "timestamp": "..." }
  ],
  "status": "in_progress",
  "testsPassedAtCheckpoint": true
}
```

### 7.8 Implementation Tasks

| Task | Description | Priority | Status |
|------|-------------|----------|--------|
| Generate unique agent IDs | UUID or incremental per session | High | ✅ Done |
| Auto-create branch on task start | `/task start <description>` command | High | ✅ Done |
| Auto-commit on tool execution | Commit after write_file, apply_patch | Medium | 🔲 Planned |
| Checkpoint command | `/checkpoint [message]` | High | ✅ Done |
| Rollback command | `/rollback [checkpoint\|commit]` | High | ✅ Done |
| Test gate before merge | Run tests, block merge if failing | High | ✅ Done |
| Branch cleanup on exit | Option to delete or keep branch | Medium | ✅ Done |
| Multi-agent coordination | Lock files, conflict detection | Low | 🔲 Planned |

---

## 8. Milestones

### Milestone 1: MVP - Core Agent with Git Safety (Target: 2 weeks)

**Goal:** A working agent that can safely perform coding tasks with rollback capability.

#### ✅ Completed (Ready to Use)

| Feature | Component | Description |
|---------|-----------|-------------|
| Core Agent Loop | `AgentLoop.cs` | Streaming/non-streaming LLM with tool calling |
| Tool System | `Tools/*.cs` | 20+ tools for file, dotnet, git, NuGet |
| Permission System | `PermissionManager.cs` | Granular read/write permissions |
| Streaming Output | `StreamResult.cs` | Real-time token display |
| Configuration | `appsettings.json` | Centralized settings |
| Health Checks | `HealthCheck.cs` | Verify LM Studio, Git, Deno, PostgreSQL |
| Retry Logic | `RetryHandler.cs` | Exponential backoff for LLM calls |
| Git Branch Isolation | `AgentSessionManager.cs` | Auto-create `agent/<id>/<task>` branches |
| Checkpoint System | `AgentSessionManager.cs` | Tag milestones, enable rollback |
| Rollback Command | `/rollback` | Reset to checkpoint or commit |
| Token Tracking | `TokenTracker.cs` | Warn at 70%/85% context usage |

#### 🔲 Remaining (To Complete MVP)

| Feature | Priority | Effort | Description |
|---------|----------|--------|-------------|
| Auto-commit on tool execution | P1 | 1 day | Commit after write_file, apply_patch |
| Integration testing | P1 | 2 days | End-to-end tests for MVP features |

#### ❌ Deferred (Not in MVP)

- MCP/Deno Sandbox
- RAG/PostgreSQL
- TUI Interface
- Multi-model orchestration
- Task Templates
- Progress Indicators
- Dry-run Mode
- Skills System

#### MVP User Story

```
Developer: "Create a Calculator class with unit tests"

Agent:
  1. ✓ Creates branch: agent/thuvu-001/calculator-class
  2. ✓ Writes Calculator.cs
  3. ✓ Commits: "feat: add Calculator class"
  4. ✓ Creates checkpoint: checkpoint-1
  5. ✓ Writes CalculatorTests.cs
  6. ✓ Runs: dotnet test → PASS
  7. ✓ Commits: "test: add Calculator tests"
  8. ✓ Merges to main (or creates PR)

If tests fail at step 6:
  → Agent rolls back to checkpoint-1
  → Retries with different approach
```

#### MVP Exit Criteria

- [ ] Agent creates isolated branch on task start
- [ ] Agent commits after each file modification
- [ ] Agent creates checkpoints at milestones
- [ ] `/rollback` command works
- [ ] Health check runs on startup
- [ ] LLM calls retry on transient failures
- [ ] Token usage displayed, warns at threshold
- [ ] All existing tests pass

---

### Milestone 2: Enhanced Safety & RAG (Target: +3 weeks)

| Feature | Description |
|---------|-------------|
| MCP/Deno Sandbox | TypeScript execution in sandbox |
| RAG Support | PostgreSQL/pgvector semantic search |
| TUI Interface | Terminal.GUI for better UX |
| Dry-run Mode | Preview changes before executing |
| Conflict Detection | Warn before problematic merges |

---

### Milestone 3: Productivity Features (Target: +3 weeks)

| Feature | Description |
|---------|-------------|
| Multi-model Orchestration | Thinking + coding model split |
| Task Templates | Pre-defined prompts for common tasks |
| Progress Indicators | Visual step tracking with ETA |
| Auto-summarize | Compress context when approaching limit |
| Cost Tracking | Token costs for paid APIs |

---

### Milestone 4: Language Expansion (Target: +4 weeks)

| Feature | Description |
|---------|-------------|
| Python Support | pip, pytest, black, mypy |
| Node.js Support | npm, jest, eslint |
| Go Support | go build, go test |
| Skills System | Save/load reusable workflows |

---

### Milestone 5: Advanced Features (Target: +6 weeks)

| Feature | Description |
|---------|-------------|
| Image/Multimodal | Process screenshots, diagrams |
| Multi-repo Index | Search across projects |
| Remote MCP Servers | External tool providers |
| Team Collaboration | Shared skills, session export |

---

## 9. Future Roadmap (Detailed)

### Phase 9: Enhanced Agent Capabilities (Priority: High)

| Task | Description | Status |
|------|-------------|--------|
| **Context Compression** | Summarize long conversations to fit context window | 🔲 Planned |
| **Multi-step Planning** | Break complex tasks into sub-tasks with checkpoints | 🔲 Planned |
| **Self-Correction** | Detect and fix errors from tool execution | 🔲 Planned |
| **Task Memory** | Remember and learn from previous sessions | 🔲 Planned |

### Phase 10: Multi-Model Orchestration (Priority: High)

| Task | Description | Status |
|------|-------------|--------|
| **Thinking/Coding Split** | Use thinking models for planning, coding models for generation | 🔲 Planned |
| **Model Router** | Auto-select best model based on task type | 🔲 Planned |
| **Fallback Chain** | Automatic fallback when model fails | 🔲 Planned |
| **Cost Optimization** | Route simple tasks to smaller/faster models | 🔲 Planned |

### Phase 10: Multi-Model Orchestration (Priority: High)

| Task | Description | Status |
|------|-------------|--------|
| **Thinking/Coding Split** | Use thinking models for planning, coding models for generation | 🔲 Planned |
| **Model Router** | Auto-select best model based on task type | 🔲 Planned |
| **Fallback Chain** | Automatic fallback when model fails | 🔲 Planned |
| **Cost Optimization** | Route simple tasks to smaller/faster models | 🔲 Planned |

### Phase 11: Language/Framework Support (Priority: Medium)

| Task | Description | Status |
|------|-------------|--------|
| **Python Support** | pip, pytest, black, mypy integration | 🔲 Planned |
| **Node.js Support** | npm, jest, eslint integration | 🔲 Planned |
| **Go Support** | go build, go test integration | 🔲 Planned |
| **Rust Support** | cargo, clippy integration | 🔲 Planned |

### Phase 12: Advanced RAG (Priority: Medium)

| Task | Description | Status |
|------|-------------|--------|
| **Code-aware Chunking** | Parse AST for better code chunks | 🔲 Planned |
| **Multi-repo Index** | Search across multiple projects | 🔲 Planned |
| **Incremental Updates** | Update index on file changes | 🔲 Planned |
| **Hybrid Search** | Combine semantic + keyword search | 🔲 Planned |

### Phase 13: Image/Multimodal Support (Priority: Low)

| Task | Description | Status |
|------|-------------|--------|
| **Image Input** | Process screenshots, diagrams | 🔲 Planned |
| **Vision Models** | Integration with local vision LLMs | 🔲 Planned |
| **Code Screenshots** | OCR code from images | 🔲 Planned |

### Phase 14: Collaboration Features (Priority: Low)

| Task | Description | Status |
|------|-------------|--------|
| **Session Export** | Save/share conversation history | 🔲 Planned |
| **Team Skills** | Share skill library across team | 🔲 Planned |
| **Remote MCP Servers** | Connect to external tool providers | 🔲 Planned |

---

## 10. Development Guidelines

### 9.1 Adding New Tools

1. **Define schema** in `Tools/BuildTools.cs`:
```csharp
new Tool
{
    Type = "function",
    Function = new FunctionDef
    {
        Name = "my_tool",
        Description = "What the tool does",
        Parameters = JsonDocument.Parse("""{ ... }""").RootElement
    }
}
```

2. **Implement logic** in `Tools/MyToolImpl.cs`

3. **Register in ToolExecutor.cs**:
```csharp
case "my_tool":
    result = await MyToolImpl.Execute(argsJson);
    break;
```

4. **Categorize risk** in `PermissionManager.cs`:
```csharp
private static readonly HashSet<string> ReadOnlyTools = new() { ..., "my_tool" };
```

5. **Create TypeScript wrapper** in `mcp/servers/`:
```typescript
export async function myTool(params: MyParams): Promise<MyResult> {
    return await __thuvu_bridge__.call('my_tool', params);
}
```

### 9.2 Testing

```bash
# Run permission system demo
/test-permissions

# Run MCP integration tests
/test-mcp

# Test specific tool
/run dotnet test --filter "ToolName"
```

### 9.3 Configuration Override

Set environment variable `LM_AGENT_CONFIG` to use custom config path.

---

## 11. Dependencies

### Required
- **.NET 8.0+**
- **LM Studio** (or OpenAI-compatible API)
- **Deno** (for MCP code execution)

### Optional
- **PostgreSQL 15+** with pgvector (for RAG)
- **Docker** (for database setup)

### NuGet Packages
- `Microsoft.Extensions.Logging`
- `Npgsql` (PostgreSQL driver)
- `Terminal.Gui` (TUI interface)

---

## 12. Quick Start Guide

### 11.1 First-Time Setup

```bash
# 1. Clone and build
git clone <repo-url>
cd thuvu
dotnet build

# 2. Start LM Studio with a tool-capable model (e.g., qwen3-coder)
# Load model on http://localhost:1234

# 3. (Optional) Start PostgreSQL for RAG
cd docker && docker-compose up -d

# 4. (Optional) Install Deno for MCP
# https://deno.land/manual/getting_started/installation

# 5. Run the agent
dotnet run
# Or with TUI: dotnet run -- --tui
```

### 11.2 Basic Usage Example

```
> Create a simple calculator class with add, subtract, multiply, divide

[Agent creates branch: agent/thuvu-001/calculator-class]
[Agent writes Calculator.cs]
[Agent runs dotnet build - success]
[Agent commits: "feat: add Calculator class with basic operations"]
[Agent creates checkpoint: agent/thuvu-001/checkpoint-1]

> Add unit tests for the calculator

[Agent writes CalculatorTests.cs]
[Agent runs dotnet test - success]
[Agent commits: "test: add unit tests for Calculator"]
[Agent merges to main]
```

---

## 13. Known Limitations

| Limitation | Workaround | Future Fix |
|------------|------------|------------|
| Single-threaded agent | Run multiple instances on different ports | Phase 8: Multi-agent |
| No web browsing | Use RAG to index documentation locally | Phase 14: Web search |
| Large files slow | Chunk large files before processing | Phase 12: Streaming |
| Context overflow | Use `/clear` to reset conversation | Phase 9: Compression |
| Windows paths only | Use WSL for Unix paths | Cross-platform paths |

---

## 14. Operational Features

### 13.1 Health Checks

Before starting, the agent verifies all required services:

```
┌─────────────────────────────────────────────────────────────┐
│                    Health Check Results                      │
├─────────────────────────────────────────────────────────────┤
│  ✅ LM Studio      http://127.0.0.1:1234    Connected       │
│  ✅ Model          qwen/qwen3-coder-30b     Loaded          │
│  ✅ Deno           v1.40.0                  Installed       │
│  ✅ PostgreSQL     localhost:5433           Connected       │
│  ⚠️  Git           v2.43.0                  No remote set   │
│  ✅ Work Directory ./work                   Writable        │
└─────────────────────────────────────────────────────────────┘
```

**Implementation:**
| Check | Method | Fallback |
|-------|--------|----------|
| LM Studio | `GET /v1/models` | Error + instructions to start |
| Model loaded | Check model in response | Warn, list available models |
| Deno | `deno --version` | Disable MCP, warn user |
| PostgreSQL | Connection test | Disable RAG, warn user |
| Git | `git --version` | Error, git required |
| Work dir | Write test file | Create directory or error |

### 13.2 Retry Logic

Auto-retry failed LLM calls with exponential backoff:

```
Attempt 1: Immediate
Attempt 2: Wait 2 seconds
Attempt 3: Wait 4 seconds
Attempt 4: Wait 8 seconds
Attempt 5: Wait 16 seconds (max)
```

**Retry conditions:**
- HTTP 429 (Rate limited)
- HTTP 500-503 (Server errors)
- Timeout errors
- Connection refused (service restarting)

**No retry:**
- HTTP 400 (Bad request - fix the request)
- HTTP 401/403 (Auth errors)
- Cancelled by user

**Configuration:**
```json
{
  "AgentConfig": {
    "MaxRetries": 5,
    "RetryBaseDelayMs": 2000,
    "RetryMaxDelayMs": 30000
  }
}
```

### 13.3 Token Budget Tracking

Monitor and warn when approaching context limits:

```
┌─────────────────────────────────────────────────────────────┐
│  Token Usage: 24,576 / 32,768 (75%)  ████████████░░░░      │
│  ⚠️  Warning: Approaching context limit                      │
│  Tip: Use /clear or /summarize to free up context           │
└─────────────────────────────────────────────────────────────┘
```

**Features:**
| Feature | Description |
|---------|-------------|
| Real-time tracking | Update after each message |
| Warning thresholds | 70% yellow, 85% red |
| Auto-summarize | Option to auto-compress at threshold |
| Token breakdown | Show system/user/assistant/tool tokens |
| Cost estimation | For paid APIs (DeepSeek, etc.) |

**Commands:**
- `/tokens` - Show current usage breakdown
- `/tokens reset` - Clear conversation (same as /clear)
- `/tokens budget <n>` - Set max tokens before warning

### 13.4 Task Templates

Pre-defined prompts for common development tasks:

```
/template list                    # Show all templates
/template use <name>              # Start task from template
/template create <name>           # Save current prompt as template
/template delete <name>           # Remove template
```

**Built-in Templates:**

| Template | Description |
|----------|-------------|
| `create-api` | Create REST API endpoint with validation |
| `add-tests` | Generate unit tests for existing code |
| `fix-bug` | Analyze and fix a reported bug |
| `refactor` | Refactor code for better maintainability |
| `add-docs` | Generate documentation for code |
| `code-review` | Review code for issues and improvements |
| `create-model` | Create data model with validation |
| `add-logging` | Add structured logging to code |
| `security-audit` | Check for common security issues |
| `performance` | Analyze and optimize performance |

**Template Format (templates/*.json):**
```json
{
  "name": "create-api",
  "description": "Create REST API endpoint",
  "prompt": "Create a REST API endpoint for {{resource}} with:\n- GET /{{resource}} - list all\n- GET /{{resource}}/{{id}} - get by id\n- POST /{{resource}} - create\n- PUT /{{resource}}/{{id}} - update\n- DELETE /{{resource}}/{{id}} - delete\n\nInclude input validation, error handling, and return appropriate HTTP status codes.",
  "variables": ["resource"],
  "autoCheckpoint": true,
  "runTestsAfter": true
}
```

### 13.5 Progress Indicators

Show estimated completion for multi-step tasks:

```
┌─────────────────────────────────────────────────────────────┐
│  Task: Create user authentication system                     │
├─────────────────────────────────────────────────────────────┤
│  [████████████████░░░░░░░░░░░░░░] 53% (Step 4/7)            │
│                                                              │
│  ✅ Step 1: Create User model                    (0:23)     │
│  ✅ Step 2: Create UserRepository                (0:45)     │
│  ✅ Step 3: Create AuthService                   (1:12)     │
│  🔄 Step 4: Create AuthController               (running)   │
│  ⬚ Step 5: Add JWT middleware                              │
│  ⬚ Step 6: Create unit tests                               │
│  ⬚ Step 7: Update documentation                            │
│                                                              │
│  Elapsed: 2:34  │  Est. remaining: 2:15                     │
└─────────────────────────────────────────────────────────────┘
```

**Features:**
| Feature | Description |
|---------|-------------|
| Step detection | Parse LLM plan into discrete steps |
| Time tracking | Measure actual time per step |
| ETA calculation | Based on average step time |
| Checkpoint auto-save | Save after each completed step |
| Resume support | Continue from last completed step |

### 13.6 Dry-Run Mode

Preview changes without executing (useful for risky operations):

```
/dryrun on                       # Enable dry-run mode
/dryrun off                      # Disable dry-run mode
/dryrun <prompt>                 # One-time dry-run
```

**Dry-run output:**
```
┌─────────────────────────────────────────────────────────────┐
│  🔍 DRY-RUN MODE - No changes will be made                  │
├─────────────────────────────────────────────────────────────┤
│                                                              │
│  Planned Actions:                                            │
│                                                              │
│  1. 📄 CREATE  src/Models/User.cs                           │
│     └─ 45 lines, User class with validation                 │
│                                                              │
│  2. 📝 MODIFY  src/Services/AuthService.cs                  │
│     └─ +23 lines, -5 lines (add user registration)          │
│                                                              │
│  3. 🗑️  DELETE  src/Models/OldUser.cs                        │
│     └─ File will be removed                                 │
│                                                              │
│  4. ⚡ EXECUTE dotnet build                                  │
│     └─ Build solution                                       │
│                                                              │
│  5. ⚡ EXECUTE dotnet test                                   │
│     └─ Run unit tests                                       │
│                                                              │
├─────────────────────────────────────────────────────────────┤
│  Risk Assessment: MEDIUM                                     │
│  - 1 file deletion                                          │
│  - 1 existing file modified                                 │
│                                                              │
│  [E]xecute  [C]ancel  [S]how details                        │
└─────────────────────────────────────────────────────────────┘
```

**Risk Levels:**
| Level | Criteria |
|-------|----------|
| LOW | Only new files, read operations |
| MEDIUM | Modifying existing files |
| HIGH | Deleting files, running processes |
| CRITICAL | Modifying system files, git push |

---

## 15. Implementation Priority Matrix

| Feature | Effort | Impact | Priority |
|---------|--------|--------|----------|
| Health checks | Low | High | **P0 - Do First** |
| Retry logic | Low | High | **P0 - Do First** |
| Token tracking | Medium | High | **P1 - Next** |
| Dry-run mode | Medium | High | **P1 - Next** |
| Task templates | Medium | Medium | **P2 - Soon** |
| Progress indicators | High | Medium | **P3 - Later** |

---

## 16. References

- [Anthropic: Code Execution with MCP](https://www.anthropic.com/engineering/code-execution-with-mcp)
- [Model Context Protocol](https://modelcontextprotocol.io/)
- [LM Studio Documentation](https://lmstudio.ai/docs)
- [pgvector](https://github.com/pgvector/pgvector)
- [Deno Security Model](https://deno.land/manual/basics/permissions)

---

## 17. Changelog

| Date | Version | Changes |
|------|---------|---------|
| 2025-12-17 | 0.0.6 | MVP implementation: HealthCheck, RetryHandler, AgentSessionManager, TokenTracker |
| 2025-12-17 | 0.0.5 | Added AGENTS.md project plan, git isolation strategy |
| 2025-12-13 | 0.0.4 | MCP Phases 1-7 complete, appsettings.json |
| 2025-12-13 | 0.0.3 | RAG support, structured logging |
| 2025-08-16 | 0.0.2 | TUI interface, permission system |
| 2025-08-01 | 0.0.1 | Initial release |