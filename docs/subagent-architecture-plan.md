# Sub-Agent Architecture Plan

## Overview

Implement a hierarchical agent system where a main agent can delegate specialized tasks to sub-agents with different roles, models, and system prompts. All interactions are recorded in SQLite for session reconstruction and workflow optimization.

---

## Phase 1: Database Schema

### 1.1 Create `messages` Table

```sql
CREATE TABLE messages (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id TEXT NOT NULL,
    parent_message_id INTEGER,          -- Self-referential FK for sub-agent hierarchy
    
    -- Timing
    started_at TEXT NOT NULL,
    completed_at TEXT,
    duration_ms INTEGER,
    
    -- Agent Info
    agent_role TEXT,                    -- 'main', 'planner', 'coder', 'tester', 'reviewer', etc.
    agent_depth INTEGER DEFAULT 0,      -- 0=main, 1=sub-agent, 2=sub-sub-agent
    model_id TEXT,
    system_prompt_id TEXT,              -- Reference to role's system prompt
    
    -- Request
    request_type TEXT,                  -- 'prompt', 'tool_call', 'delegation', 'continuation'
    request_content TEXT,               -- User prompt or delegation task
    context_mode TEXT,                  -- 'full', 'summary', 'selective' (for future)
    context_token_count INTEGER,
    
    -- Response
    response_content TEXT,              -- Full LLM response
    response_summary TEXT,              -- Condensed summary for parent
    tool_calls_json TEXT,               -- JSON array of tool calls
    files_modified_json TEXT,           -- JSON array of files touched
    
    -- Metrics
    prompt_tokens INTEGER,
    completion_tokens INTEGER,
    total_tokens INTEGER,
    iteration_count INTEGER DEFAULT 0,  -- How many LLM iterations within this call
    
    -- Bailout tracking
    max_iterations INTEGER,             -- Limit set for this call
    max_duration_ms INTEGER,            -- Time limit set for this call
    bailout_reason TEXT,                -- If stopped early: 'timeout', 'max_iterations', 'error'
    
    -- Status
    status TEXT DEFAULT 'pending',      -- 'pending', 'running', 'completed', 'failed', 'cancelled', 'timeout'
    error_message TEXT,
    
    -- Metadata
    metadata_json TEXT,
    
    FOREIGN KEY (parent_message_id) REFERENCES messages(id),
    FOREIGN KEY (session_id) REFERENCES sessions(session_id)
);

CREATE INDEX idx_messages_session ON messages(session_id);
CREATE INDEX idx_messages_parent ON messages(parent_message_id);
CREATE INDEX idx_messages_role ON messages(agent_role);
CREATE INDEX idx_messages_depth ON messages(agent_depth);
CREATE INDEX idx_messages_started ON messages(started_at);
CREATE INDEX idx_messages_status ON messages(status);
```

### 1.2 Files to Modify
- `Tools/SqliteService.cs` - Add messages table schema and CRUD operations

---

## Phase 2: Role Configuration

### 2.1 Config Structure in `appsettings.json`

```json
{
  "AgentRoles": {
    "MaxDepth": 2,
    "DefaultMaxIterations": 20,
    "DefaultMaxDurationMs": 300000,
    "Roles": [
      {
        "RoleId": "main",
        "DisplayName": "Main Agent",
        "ModelId": "qwen/qwen3-coder-30b",
        "SystemPromptFile": "prompts/roles/main.md",
        "CanDelegate": true,
        "AllowedDelegations": ["planner", "coder", "tester", "reviewer", "debugger"],
        "MaxIterations": 50,
        "MaxDurationMs": 600000
      },
      {
        "RoleId": "coder",
        "DisplayName": "Coder",
        "ModelId": "qwen/qwen3-coder-30b",
        "SystemPromptFile": "prompts/roles/coder.md",
        "CanDelegate": true,
        "AllowedDelegations": ["tester"],
        "MaxIterations": 30,
        "MaxDurationMs": 300000
      },
      {
        "RoleId": "tester",
        "DisplayName": "Tester",
        "ModelId": "deepseek-chat",
        "SystemPromptFile": "prompts/roles/tester.md",
        "CanDelegate": false,
        "MaxIterations": 20,
        "MaxDurationMs": 180000
      },
      {
        "RoleId": "reviewer",
        "DisplayName": "Code Reviewer",
        "ModelId": "anthropic/claude-opus-4.5",
        "SystemPromptFile": "prompts/roles/reviewer.md",
        "CanDelegate": false,
        "MaxIterations": 10,
        "MaxDurationMs": 120000
      },
      {
        "RoleId": "planner",
        "DisplayName": "Task Planner",
        "ModelId": "deepseek-reasoner",
        "SystemPromptFile": "prompts/roles/planner.md",
        "CanDelegate": true,
        "AllowedDelegations": ["coder", "tester", "reviewer"],
        "MaxIterations": 15,
        "MaxDurationMs": 180000
      },
      {
        "RoleId": "debugger",
        "DisplayName": "Debugger",
        "ModelId": "qwen/qwen3-coder-30b",
        "SystemPromptFile": "prompts/roles/debugger.md",
        "CanDelegate": false,
        "MaxIterations": 25,
        "MaxDurationMs": 240000
      }
    ]
  }
}
```

### 2.2 Files to Create
- `Models/AgentRoleConfig.cs` - Configuration models
- `prompts/roles/main.md` - Main agent system prompt with delegation rules
- `prompts/roles/coder.md` - Coder role system prompt
- `prompts/roles/tester.md` - Tester role system prompt
- `prompts/roles/reviewer.md` - Reviewer role system prompt
- `prompts/roles/planner.md` - Planner role system prompt
- `prompts/roles/debugger.md` - Debugger role system prompt

---

## Phase 3: Delegation Tool

### 3.1 Tool Definition

```json
{
  "name": "delegate_to_agent",
  "description": "Delegate a specialized task to a sub-agent with a specific role. Use this when a task requires specialized expertise. The sub-agent will work on the task and return a structured result.",
  "parameters": {
    "role": {
      "type": "string",
      "enum": ["coder", "tester", "reviewer", "planner", "debugger"],
      "description": "The role/specialization of the sub-agent"
    },
    "task": {
      "type": "string",
      "description": "Clear description of what the sub-agent should accomplish"
    },
    "context_files": {
      "type": "array",
      "items": {"type": "string"},
      "description": "Optional: specific files the sub-agent should focus on"
    },
    "success_criteria": {
      "type": "string",
      "description": "Optional: how to determine if the task was successful"
    }
  },
  "required": ["role", "task"]
}
```

### 3.2 Sub-Agent Response Format

Sub-agents return structured JSON to their parent:

```json
{
  "status": "completed",
  "summary": "Implemented JWT authentication middleware with token validation and refresh logic",
  "details": "Created JwtMiddleware.cs with ValidateToken() and RefreshToken() methods. Added configuration for secret key and expiry. Integrated with existing auth pipeline.",
  "files_modified": [
    "Middleware/JwtMiddleware.cs",
    "appsettings.json",
    "Program.cs"
  ],
  "files_created": [
    "Middleware/JwtMiddleware.cs"
  ],
  "tests_run": 0,
  "tests_passed": 0,
  "warnings": [],
  "suggestions": [
    "Consider adding rate limiting to token refresh endpoint",
    "Add unit tests for edge cases"
  ],
  "iteration_count": 8,
  "duration_ms": 45000
}
```

### 3.3 Files to Create/Modify
- `Tools/DelegationToolImpl.cs` - Tool implementation
- `Tools/BuildTools.cs` - Add tool definition
- `ToolExecutor.cs` - Add dispatch

---

## Phase 4: Sub-Agent Executor

### 4.1 SubAgentExecutor Class

```csharp
public class SubAgentExecutor
{
    // Execute a sub-agent task synchronously
    public async Task<SubAgentResult> ExecuteAsync(
        SubAgentRequest request,
        List<ChatMessage> parentContext,  // Full context from parent
        int currentDepth,
        CancellationToken ct);
    
    // Track all LLM calls within sub-agent
    private async Task RecordMessageAsync(MessageRecord record);
    
    // Check bailout conditions
    private bool ShouldBailout(int iterations, long elapsedMs, AgentRole role);
    
    // Build sub-agent system prompt
    private string BuildSubAgentSystemPrompt(AgentRole role, string task, int depth);
}
```

### 4.2 Sub-Agent Awareness

Sub-agent system prompts include:
```markdown
## Sub-Agent Context

You are operating as a **{{ROLE}}** sub-agent (depth {{DEPTH}}/{{MAX_DEPTH}}).
- Parent agent: {{PARENT_ROLE}}
- Your task: {{TASK}}
- Success criteria: {{CRITERIA}}

## Response Requirements

When you complete your task, provide a structured response:
1. Summary (1-2 sentences of what you accomplished)
2. Details (technical specifics)
3. Files modified/created
4. Any warnings or suggestions for the parent

## Constraints

- Max iterations: {{MAX_ITERATIONS}}
- Time limit: {{MAX_DURATION}}
- You {{CAN/CANNOT}} delegate to other sub-agents
{{#IF CAN_DELEGATE}}
- Available delegations: {{ALLOWED_DELEGATIONS}}
{{/IF}}
```

### 4.3 Files to Create
- `Models/SubAgentExecutor.cs` - Core execution logic
- `Models/SubAgentRequest.cs` - Request model
- `Models/SubAgentResult.cs` - Result model

---

## Phase 5: Message Recording Service

### 5.1 MessageRecordingService

```csharp
public class MessageRecordingService
{
    // Start a new message record (returns ID)
    public async Task<long> StartMessageAsync(MessageStartInfo info);
    
    // Update with response when complete
    public async Task CompleteMessageAsync(long messageId, MessageCompleteInfo info);
    
    // Mark as failed
    public async Task FailMessageAsync(long messageId, string error);
    
    // Get message hierarchy for session
    public async Task<List<MessageRecord>> GetSessionMessagesAsync(string sessionId);
    
    // Get children of a message
    public async Task<List<MessageRecord>> GetChildMessagesAsync(long parentId);
}
```

### 5.2 Integration Points
- `AgentLoop.cs` - Record each LLM call
- `SubAgentExecutor.cs` - Record delegation and sub-agent calls
- `WebAgentService.cs` - Initialize recording for web sessions

### 5.3 Files to Create
- `Models/MessageRecordingService.cs`
- `Models/MessageRecord.cs`

---

## Phase 6: Web UI Updates

### 6.1 Message Display with Hierarchy

```
┌─────────────────────────────────────────────────────┐
│ 👤 User: Add authentication to the API              │
├─────────────────────────────────────────────────────┤
│ 🤖 Main Agent:                                      │
│   Analyzing requirements...                         │
│   I'll delegate the implementation to a coder.     │
│                                                     │
│   ┌─────────────────────────────────────────────┐  │
│   │ 👨‍💻 Coder (sub-agent, depth 1):              │  │
│   │   Created JWT middleware...                  │  │
│   │   Files: JwtMiddleware.cs, Program.cs        │  │
│   │   ✅ Completed in 45s (8 iterations)         │  │
│   │                                              │  │
│   │   ┌─────────────────────────────────────┐   │  │
│   │   │ 🧪 Tester (sub-agent, depth 2):     │   │  │
│   │   │   Wrote 5 unit tests, all passing   │   │  │
│   │   │   ✅ Completed in 20s (4 iterations)│   │  │
│   │   └─────────────────────────────────────┘   │  │
│   └─────────────────────────────────────────────┘  │
│                                                     │
│   Authentication implemented successfully.          │
│   Summary: Created JWT auth with tests.             │
└─────────────────────────────────────────────────────┘
```

### 6.2 Visual Indicators
- 🤖 Main agent
- 👨‍💻 Coder
- 🧪 Tester
- 👀 Reviewer
- 📋 Planner
- 🔧 Debugger
- Indentation based on depth
- Collapsible sub-agent sections
- Status badges (✅ completed, ⏱️ running, ❌ failed, ⚠️ timeout)

### 6.3 Files to Modify
- `Web/Components/Chat.razor` - Add hierarchical message rendering
- `wwwroot/css/app.css` - Add indentation and role styling

---

## Phase 7: Main Agent System Prompt Updates

### 7.1 Delegation Rules for Main Agent

Add to main agent system prompt:

```markdown
## Delegation Guidelines

You can delegate specialized tasks to sub-agents using the `delegate_to_agent` tool.

### When to Delegate

**Delegate to CODER when:**
- Implementing new features or components
- Writing or modifying multiple files
- Complex refactoring tasks

**Delegate to TESTER when:**
- Writing unit or integration tests
- Running and analyzing test results
- Test-driven development tasks

**Delegate to REVIEWER when:**
- Code review is needed before committing
- Security analysis required
- Architecture decisions need validation

**Delegate to PLANNER when:**
- Complex multi-step tasks need breakdown
- Architecture decisions required
- Project planning or roadmap work

**Delegate to DEBUGGER when:**
- Investigating failing tests
- Tracing runtime errors
- Performance issues

### When NOT to Delegate

- Simple questions or explanations
- Single file modifications
- Quick fixes (< 5 minutes of work)
- Tasks requiring user interaction

### Delegation Best Practices

1. Provide clear, specific task descriptions
2. Include relevant file paths if known
3. Define success criteria when possible
4. Review sub-agent results before proceeding
```

---

## Implementation Order

### Sprint 1: Foundation (Phase 1-2)
- [ ] Add messages table to SQLite schema
- [ ] Create AgentRoleConfig model and loader
- [ ] Create role system prompt files
- [ ] Add roles configuration to appsettings.json

### Sprint 2: Core Execution (Phase 3-4)
- [ ] Implement delegate_to_agent tool
- [ ] Create SubAgentExecutor
- [ ] Implement bailout logic (iterations, timeout)
- [ ] Build sub-agent system prompt generation

### Sprint 3: Recording (Phase 5)
- [ ] Create MessageRecordingService
- [ ] Integrate recording into AgentLoop
- [ ] Integrate recording into SubAgentExecutor
- [ ] Add message retrieval methods

### Sprint 4: Integration (Phase 6-7)
- [ ] Update Web UI for hierarchical display
- [ ] Add role indicators and styling
- [ ] Update main agent system prompt
- [ ] Add collapsible sub-agent sections

### Sprint 5: Testing & Polish
- [ ] End-to-end testing of delegation flows
- [ ] Performance testing with deep hierarchies
- [ ] UI polish and edge case handling
- [ ] Documentation

---

## Configuration Summary

| Role | Default Model | Max Iterations | Max Duration | Can Delegate |
|------|---------------|----------------|--------------|--------------|
| main | qwen3-coder-30b | 50 | 10 min | ✅ (all) |
| coder | qwen3-coder-30b | 30 | 5 min | ✅ (tester) |
| tester | deepseek-chat | 20 | 3 min | ❌ |
| reviewer | claude-opus | 10 | 2 min | ❌ |
| planner | deepseek-reasoner | 15 | 3 min | ✅ (coder, tester, reviewer) |
| debugger | qwen3-coder-30b | 25 | 4 min | ❌ |

---

## Open Questions / Future Enhancements

1. **Context modes**: Implement 'summary' and 'selective' context passing
2. **Tool restrictions**: Per-role tool allowlists/denylists
3. **Parallel sub-agents**: Allow concurrent delegations
4. **Result validation**: Automatic validation of sub-agent outputs
5. **Learning**: Track delegation success rates for optimization
6. **Cost tracking**: Per-role token usage and cost estimates
