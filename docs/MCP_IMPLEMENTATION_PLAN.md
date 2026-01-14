# MCP Code Execution Implementation Plan for THUVU

## Overview

This plan outlines the implementation of Anthropic's MCP (Model Context Protocol) code execution paradigm for THUVU. Instead of traditional JSON-based tool calling (one tool at a time), the agent will write TypeScript code that composes multiple tools, executes in a sandbox, and returns only relevant results.

**Key Benefits:**
- Up to 98.7% token reduction
- Batch multiple operations in a single execution
- Progressive tool discovery (load only what's needed)
- Data stays in sandbox; only summaries returned to LLM

---

## Phase 1: TypeScript Tool Wrappers (Week 1-2)

### Goal
Create TypeScript wrapper modules for all existing C# tools, exposing them as importable functions.

### Directory Structure
```
thuvu/
├── mcp/
│   ├── servers/
│   │   ├── filesystem/
│   │   │   ├── readFile.ts
│   │   │   ├── writeFile.ts
│   │   │   ├── searchFiles.ts
│   │   │   └── index.ts
│   │   ├── git/
│   │   │   ├── status.ts
│   │   │   ├── commit.ts
│   │   │   ├── diff.ts
│   │   │   └── index.ts
│   │   ├── dotnet/
│   │   │   ├── build.ts
│   │   │   ├── test.ts
│   │   │   ├── new.ts
│   │   │   └── index.ts
│   │   ├── rag/
│   │   │   ├── index.ts
│   │   │   ├── search.ts
│   │   │   └── stats.ts
│   │   └── process/
│   │       ├── run.ts
│   │       └── index.ts
│   ├── runtime/
│   │   ├── sandbox.ts        # Deno/Node sandbox executor
│   │   ├── bridge.ts         # C# <-> TypeScript IPC
│   │   └── permissions.ts    # Security policies
│   └── types/
│       └── tools.d.ts        # Type definitions
```

### Tool Wrapper Example
```typescript
// mcp/servers/filesystem/readFile.ts
export interface ReadFileResult {
  content: string;
  sha256: string;
  encoding: string;
}

export async function readFile(path: string): Promise<ReadFileResult> {
  // Calls back to C# via IPC bridge
  return await __thuvu_bridge__.call('read_file', { path });
}
```

### Tasks
1. [x] Create `mcp/` directory structure
2. [x] Define TypeScript interfaces for all tool inputs/outputs (`types/tools.d.ts`)
3. [x] Create wrapper modules for each existing tool:
   - [x] `filesystem/readFile.ts` - wraps ReadFileToolImpl
   - [x] `filesystem/writeFile.ts` - wraps WriteFileToolImpl
   - [x] `filesystem/searchFiles.ts` - wraps SearchFilesToolImpl
   - [x] `filesystem/applyPatch.ts` - wraps ApplyPatchToolImpl
   - [x] `git/status.ts`, `git/commit.ts`, `git/diff.ts`
   - [x] `dotnet/build.ts`, `dotnet/test.ts`, `dotnet/new.ts`
   - [x] `rag/search.ts`, `rag/index.ts`, `rag/stats.ts`
   - [x] `process/run.ts` - wraps RunProcessToolImpl
4. [x] Create barrel exports (`index.ts`) for each server

---

## Phase 2: TypeScript Execution Sandbox (Week 2-3)

### Goal
Create a secure sandbox environment to execute agent-generated TypeScript code.

### Options Evaluated

| Runtime | Pros | Cons |
|---------|------|------|
| **Deno** | Built-in sandboxing, TypeScript native, permissions model | Requires Deno installation |
| **Node.js + vm2** | Widely available | vm2 deprecated, security concerns |
| **isolated-vm** | V8 isolates, good security | No async I/O, complex setup |
| **QuickJS** | Lightweight, embeddable | Limited ecosystem |

### Recommended: Deno
- Native TypeScript support
- Fine-grained permissions (`--allow-read`, `--allow-net`, etc.)
- Can be bundled or run as subprocess
- Active security maintenance

### Architecture
```
┌─────────────────────────────────────────────────────────────┐
│                        THUVU (C#)                           │
├─────────────────────────────────────────────────────────────┤
│  AgentLoop                                                  │
│    │                                                        │
│    ▼                                                        │
│  MCPCodeExecutor                                            │
│    │                                                        │
│    ├──► Spawn Deno subprocess                               │
│    │      --allow-read=./                                   │
│    │      --allow-write=./                                  │
│    │                                                        │
│    ├──► IPC Bridge (stdin/stdout JSON-RPC)                  │
│    │                                                        │
│    └──► Collect results, return to LLM                      │
└─────────────────────────────────────────────────────────────┘
         │
         ▼
┌─────────────────────────────────────────────────────────────┐
│                    Deno Sandbox                             │
├─────────────────────────────────────────────────────────────┤
│  import { readFile } from './servers/filesystem/readFile';  │
│  import { searchFiles } from './servers/filesystem/search'; │
│                                                             │
│  // Agent-generated code                                    │
│  const files = await searchFiles({ glob: '**/*.cs' });      │
│  const contents = await Promise.all(                        │
│    files.slice(0, 5).map(f => readFile(f))                  │
│  );                                                         │
│  return { fileCount: files.length, preview: contents };     │
└─────────────────────────────────────────────────────────────┘
```

### Tasks
1. [x] Create `McpCodeExecutor.cs` class
   - [x] Spawn Deno subprocess with restricted permissions
   - [x] Implement JSON-RPC IPC protocol over stdin/stdout
   - [x] Handle timeouts and cancellation
   - [x] Capture and return execution results
2. [x] Create `mcp/runtime/sandbox.ts`
   - [x] Bootstrap script that loads agent code
   - [x] Expose `__thuvu_bridge__` global for tool calls
   - [x] Handle errors gracefully
3. [x] Create `mcp/runtime/bridge.ts`
   - [x] JSON-RPC message handling
   - [x] Route tool calls to C# and return results
4. [x] Create `mcp/runtime/permissions.ts`
   - [x] Define allowed operations per tool
   - [x] Path sandboxing (restrict to project directory)

---

## Phase 3: C# Bridge Implementation (Week 3-4)

### Goal
Implement the C# side of the IPC bridge that handles tool call requests from the TypeScript sandbox.

### New Files
```csharp
// Models/McpBridge.cs
public class McpBridge
{
    public async Task<string> HandleToolCall(string toolName, string argsJson);
    public void RegisterTool(string name, Func<string, Task<string>> handler);
}

// Models/McpCodeExecutor.cs
public class McpCodeExecutor
{
    public async Task<McpExecutionResult> ExecuteAsync(
        string typeScriptCode,
        CancellationToken ct,
        TimeSpan timeout);
}

// Models/McpExecutionResult.cs
public class McpExecutionResult
{
    public bool Success { get; init; }
    public string? Result { get; init; }
    public string? Error { get; init; }
    public List<ToolCallLog> ToolCalls { get; init; }
    public TimeSpan Duration { get; init; }
}
```

### Tasks
1. [x] Create `Models/McpBridge.cs`
   - [x] Register all existing tools
   - [x] Handle JSON-RPC requests
   - [x] Return results or errors
2. [x] Create `Models/McpCodeExecutor.cs`
   - [x] Process spawning with Deno
   - [x] Bidirectional IPC communication
   - [x] Timeout handling
   - [x] Resource cleanup
3. [x] Create `Models/McpExecutionResult.cs`
4. [x] Integrate with existing tool implementations
   - [x] Refactor tools to be callable from both JSON schema and MCP bridge

---

## Phase 4: Agent Integration (Week 4-5)

### Goal
Modify the agent loop to use MCP code execution mode when beneficial.

### New System Prompt (Code Mode)
```
You are a coding agent with access to tools via TypeScript code execution.

Instead of calling tools one at a time, write TypeScript code that:
1. Imports needed tools from the servers/ directory
2. Composes operations efficiently (batch reads, parallel operations)
3. Processes data locally and returns only relevant results

Available servers:
- filesystem: readFile, writeFile, searchFiles, applyPatch
- git: status, commit, diff
- dotnet: build, test, new
- rag: search, index, stats
- process: run

Example:
```typescript
import { searchFiles, readFile } from './servers/filesystem';

const csFiles = await searchFiles({ glob: '**/*.cs' });
const contents = await Promise.all(
  csFiles.slice(0, 3).map(f => readFile(f.path))
);
return {
  totalFiles: csFiles.length,
  samples: contents.map(c => ({ path: c.path, lines: c.content.split('\n').length }))
};
```

Write code to accomplish the task, then I'll execute it and show you the results.
```

### Hybrid Mode Strategy
- **Simple queries** (single tool): Use traditional JSON tool calling
- **Complex workflows** (multiple tools, data processing): Use MCP code execution
- **User preference**: `/mcp on|off` command to toggle

### Tasks
1. [x] Add `/mcp` command to toggle code execution mode (`/mcp on|off`)
2. [x] Create new system prompt for code execution mode (`McpSystemPrompts.cs`)
3. [x] Modify agent loop to detect TypeScript code blocks and route to MCP executor
4. [x] Implement result parsing and display
5. [x] Add tool discovery endpoint (`/mcp tools`)
6. [x] Update help text and documentation

---

## Phase 5: Progressive Tool Discovery (Week 5-6)

### Goal
Implement lazy loading of tool definitions to minimize context usage.

### Tool Catalog
```typescript
// mcp/catalog.ts
export interface ToolInfo {
  name: string;
  server: string;
  description: string;
  signature: string;
}

export async function searchTools(query: string): Promise<ToolInfo[]> {
  // Search tool descriptions, return matching tools
}

export async function getToolSchema(server: string, tool: string): Promise<object> {
  // Return full JSON schema for a specific tool
}
```

### Tasks
1. [x] Create `mcp/catalog.ts` with tool metadata
2. [x] Implement `searchTools` function
3. [x] Implement `getToolSchema` function
4. [x] Update system prompt to use progressive discovery
5. [x] Add catalog tools to MCP bridge (catalog_list, catalog_search, catalog_schema)

---

## Phase 6: Agent Skills (Week 6-7)

### Goal
Allow agents to save and reuse TypeScript workflows.

### Skills Storage
```
thuvu/
├── skills/
│   ├── analyze-codebase.ts
│   ├── refactor-function.ts
│   ├── run-tests-and-fix.ts
│   └── index.json  # skill registry
```

### Skill Interface
```typescript
// skills/analyze-codebase.ts
export const metadata = {
  name: 'analyze-codebase',
  description: 'Analyze project structure and dependencies',
  version: '1.0.0'
};

export async function execute(params: { depth?: number }) {
  // Reusable workflow code
}
```

### Tasks
1. [x] Create skills directory structure
2. [x] Implement skill registry (`index.json`)
3. [x] Add `/mcp skill save <name>` command
4. [x] Add `/mcp skill run <name>` command
5. [x] Add `/mcp skill list` command
6. [x] Add `/mcp skill delete <name>` command
7. [x] Create example skills (analyze-codebase, run-tests-and-fix)

---

## Phase 7: Security Hardening (Week 7-8)

### Goal
Ensure the sandbox is secure against malicious or accidental harm.

### Security Measures
1. **Path Sandboxing**: Restrict file operations to project directory
2. **Network Restrictions**: No network access by default
3. **Resource Limits**: CPU time, memory limits
4. **Code Review**: Optionally require user approval for generated code
5. **Audit Logging**: Log all tool calls and results

### Permission Levels
```typescript
// mcp/runtime/permissions.ts
export enum PermissionLevel {
  ReadOnly = 'readonly',      // Only read operations
  ReadWrite = 'readwrite',    // Read + write in project dir
  Execute = 'execute',        // Can run processes
  Full = 'full'               // All permissions (requires approval)
}
```

### Tasks
1. [x] Implement path validation in bridge (ValidatePaths, IsPathSafe methods)
2. [x] Add Deno permission flags based on permission level (in McpCodeExecutor)
3. [x] Implement resource limits (timeout, memory in McpConfig)
4. [x] Add code preview and approval flow (TryExecuteMcpCodeBlockAsync)
5. [x] Create audit log for MCP executions (LogToolCall in McpBridge)
6. [x] Add `/mcp permissions` command
7. [x] Add `/mcp audit` command

---

## Implementation Timeline

| Week | Phase | Deliverables | Status |
|------|-------|--------------|--------|
| 1-2 | TypeScript Wrappers | All tool wrappers, type definitions | ✅ Complete |
| 2-3 | Sandbox | Deno integration, IPC protocol | ✅ Complete |
| 3-4 | C# Bridge | McpBridge, McpCodeExecutor | ✅ Complete |
| 4-5 | Agent Integration | Hybrid mode, new prompts | ✅ Complete |
| 5-6 | Tool Discovery | Catalog, searchTools | ✅ Complete |
| 6-7 | Skills | Save/load workflows | ✅ Complete |
| 7-8 | Security | Permissions, audit, hardening | ✅ Complete |

---

## Dependencies

### Required
- **Deno** runtime (v1.40+) - https://deno.land
- **TypeScript** 5.0+ type definitions

### NuGet Packages (C#)
- None additional required (uses Process for Deno)

### Optional
- `StreamJsonRpc` - For more robust JSON-RPC (Microsoft package)

---

## Configuration

### appsettings.json additions
```json
{
  "Mcp": {
    "Enabled": false,
    "DenoPath": "deno",
    "DefaultTimeout": 300000,
    "MaxMemoryMb": 512,
    "PermissionLevel": "readwrite",
    "SkillsDirectory": "./skills",
    "AuditLog": true
  }
}
```

---

## Success Metrics

1. **Token Reduction**: Measure tokens used per task (target: 50%+ reduction)
2. **Execution Time**: Compare MCP vs traditional tool calling
3. **Error Rate**: Track sandbox failures and tool errors
4. **User Adoption**: Monitor `/mcp on` usage

---

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| Deno not installed | Graceful fallback to traditional mode |
| Sandbox escape | Strict Deno permissions, code review |
| Performance overhead | Cache Deno process, warm starts |
| Complex debugging | Detailed logging, step-through mode |
| LLM generates bad code | Syntax validation, sandboxed execution |

---

## Future Enhancements

1. **Multi-language support**: Python, Go execution environments
2. **Remote MCP servers**: Connect to external tool providers
3. **Skill marketplace**: Share skills between users
4. **Visual workflow editor**: GUI for creating skills
5. **Streaming results**: Real-time output from long-running code

---

## References

- [Anthropic: Code Execution with MCP](https://www.anthropic.com/engineering/code-execution-with-mcp)
- [Model Context Protocol Specification](https://modelcontextprotocol.io/)
- [Deno Security Model](https://deno.land/manual/basics/permissions)
- [JSON-RPC 2.0 Specification](https://www.jsonrpc.org/specification)
