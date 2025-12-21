# Multi-Agent Orchestration Guide

## Overview

T.H.U.V.U. supports multi-agent orchestration for complex tasks. The system automatically decomposes tasks into subtasks, determines parallelization opportunities, and coordinates multiple agents to execute the plan efficiently.

## Quick Start

```bash
# 1. Decompose a task into subtasks (saves to work directory)
/plan Create a REST API for user management with CRUD operations

# 2. Review the plan (saved as current-plan.json and current-plan.md)

# 3. Execute with multiple agents
/orchestrate
```

## Commands

### `/plan <task description>`

Analyzes a task and creates a decomposition plan. The plan is saved to the work directory.

**Subcommands:**
```
/plan <description>     # Create new plan
/plan load [file]       # Load plan from file (default: current-plan.json)
/plan show              # Show the current loaded plan
/plan help              # Show help
```

**Examples:**
```
/plan Add user authentication with JWT tokens
/plan Refactor the database layer to use repository pattern
/plan load my-saved-plan.json
```

**Output Files:**
- `current-plan.json` - Machine-readable plan for orchestration
- `current-plan.md` - Human-readable markdown with status tracking

**Plan Contents:**
- List of subtasks with IDs, descriptions, and estimates
- Dependency graph showing execution phases
- Recommended number of agents
- Risk assessment
- Parallelization strategy

### `/orchestrate [options]`

Executes the plan from file with multiple agents.

**Options:**
| Option | Description | Default |
|--------|-------------|---------|
| `--agents N` | Override agent count (1-8) | Plan recommendation |
| `--no-merge` | Skip auto-merging agent branches | Auto-merge enabled |
| `--plan FILE` | Use specific plan file | `current-plan.json` |

**Examples:**
```
/orchestrate                       # Use current-plan.json with defaults
/orchestrate --agents 4            # Force 4 agents
/orchestrate --no-merge            # Keep branches separate for review
/orchestrate --plan my-plan.json   # Use specific plan file
```

**Progress Tracking:**
- Plan file is updated as tasks complete (status changes)
- `orchestration-progress.json` contains execution results
- Markdown file shows visual status (✅ ❌ 🔄)

## Architecture

### Components

```
┌─────────────────────────────────────────────────────────────────┐
│                      User Request                                │
│                "Create user API with auth"                       │
└──────────────────────────┬──────────────────────────────────────┘
                           │
                           ▼
┌─────────────────────────────────────────────────────────────────┐
│                    TaskDecomposer                                │
│  - Analyzes task complexity                                      │
│  - Identifies subtasks and dependencies                          │
│  - Estimates time and resources                                  │
│  - Recommends agent count                                        │
└──────────────────────────┬──────────────────────────────────────┘
                           │
                           ▼
┌─────────────────────────────────────────────────────────────────┐
│                       TaskPlan                                   │
│  ┌─────────┐  ┌─────────┐  ┌─────────┐  ┌─────────┐            │
│  │ Task t1 │  │ Task t2 │  │ Task t3 │  │ Task t4 │            │
│  │ Analyze │→ │ Model   │→ │ Service │→ │ Tests   │            │
│  └─────────┘  └─────────┘  └─────────┘  └─────────┘            │
│       │            │            │            │                   │
│       │      ┌─────┴─────┐      │            │                   │
│       │      │ Parallel  │      │            │                   │
│       │      └───────────┘      │            │                   │
└──────────────────────────┬──────────────────────────────────────┘
                           │
                           ▼
┌─────────────────────────────────────────────────────────────────┐
│                    TaskOrchestrator                              │
│  - Manages agent pool                                            │
│  - Assigns tasks to agents                                       │
│  - Handles phase transitions                                     │
│  - Collects and merges results                                   │
└──────────────────────────┬──────────────────────────────────────┘
                           │
          ┌────────────────┼────────────────┐
          ▼                ▼                ▼
    ┌──────────┐    ┌──────────┐    ┌──────────┐
    │ Agent 1  │    │ Agent 2  │    │ Agent 3  │
    │ Task t2  │    │ Task t3  │    │  (idle)  │
    │ Branch A │    │ Branch B │    │          │
    └──────────┘    └──────────┘    └──────────┘
          │                │
          └────────┬───────┘
                   ▼
           ┌─────────────┐
           │  Git Merge  │
           │   Results   │
           └─────────────┘
```

### Agent Pool

The agent pool manages concurrent agent instances:

- **Max Agents**: Configurable limit (default: 4)
- **Agent Lifecycle**: Idle → Running → Completed/Failed → Idle
- **Work Isolation**: Each agent gets its own work directory
- **Branch Isolation**: Each agent works on a separate git branch

### Execution Phases

Tasks are grouped into phases based on dependencies:

```
Phase 1: [t1: Analyze codebase]           ← Must complete first
            │
Phase 2: [t2: Create model] [t3: Create repo]  ← Can run in parallel
            │                    │
Phase 3: [t4: Create service]              ← Depends on t2 and t3
            │
Phase 4: [t5: Add tests]                   ← Depends on t4
```

## Subtask Types

| Type | Icon | Description |
|------|------|-------------|
| Analysis | `[A]` | Reading/understanding code |
| Planning | `[P]` | Designing solutions |
| Implementation | `[I]` | Writing code |
| Testing | `[T]` | Writing/running tests |
| Review | `[R]` | Code review, validation |
| Documentation | `[D]` | Writing docs |
| Refactoring | `[F]` | Improving existing code |
| Configuration | `[C]` | Config changes, setup |

## Complexity Levels

| Level | Color | Typical Duration |
|-------|-------|------------------|
| Trivial | Green | < 2 minutes |
| Simple | Green | 2-5 minutes |
| Medium | Yellow | 5-15 minutes |
| Complex | Red | 15-30 minutes |
| VeryComplex | Magenta | 30+ minutes |

## Configuration

### OrchestratorConfig

```csharp
{
    MaxAgents: 4,              // Maximum concurrent agents
    AgentTimeoutMinutes: 30,   // Timeout per subtask
    UseProcessIsolation: true, // Spawn separate processes
    AutoMergeResults: true,    // Merge branches on success
    BaseBranch: "main",        // Base branch for orchestration
    RequireTestsPass: true     // Gate merges on test success
}
```

### Environment Variables

| Variable | Description |
|----------|-------------|
| `THUVU_AGENT_ID` | Unique ID assigned to agent |
| `THUVU_ORCHESTRATED` | Set to "true" when running under orchestrator |

## Git Branch Strategy

During orchestration, branches are created:

```
main
  └── orchestration/{plan-id}           # Orchestration base
        ├── agent/{plan-id}/agent-001/t1  # Agent 1's work
        ├── agent/{plan-id}/agent-002/t2  # Agent 2's work
        └── agent/{plan-id}/agent-001/t3  # Agent 1's next task
```

After successful completion:
1. All agent branches are merged into orchestration branch
2. Orchestration branch can be merged to main (manual or auto)

## Example Session

```
> /plan Create a calculator library with unit tests

╔══════════════════════════════════════════════════════════════════════════════╗
║ Task Decomposition Plan                                                      ║
╠══════════════════════════════════════════════════════════════════════════════╣
║ Task: Create a calculator library with unit tests                            ║
║ Summary: Build a Calculator class with basic operations and comprehensive... ║
║                                                                              ║
║ Recommended Agents: 2  |  Est. Time: 25 min  |  Subtasks: 5                  ║
╠══════════════════════════════════════════════════════════════════════════════╣
║ ── Phase 1 ──                                                                ║
║   [A] t1: Analyze project structure                              ~3min       ║
║ ── Phase 2 (can run 2 in parallel) ──                                        ║
║   [I] t2: Create Calculator class with basic operations          ~8min       ║
║       └─ depends on: t1                                                      ║
║   [I] t3: Create AdvancedCalculator with scientific functions    ~8min       ║
║       └─ depends on: t1                                                      ║
║ ── Phase 3 ──                                                                ║
║   [T] t4: Create unit tests for Calculator                       ~5min       ║
║       └─ depends on: t2                                                      ║
║   [T] t5: Create unit tests for AdvancedCalculator               ~5min       ║
║       └─ depends on: t3                                                      ║
╠══════════════════════════════════════════════════════════════════════════════╣
║ Risk Assessment:                                                             ║
║   Low risk - new files only, no modifications to existing code               ║
╠══════════════════════════════════════════════════════════════════════════════╣
║ Parallelization Strategy:                                                    ║
║   Phase 2 and Phase 3 can each run 2 tasks in parallel. Use 2 agents for    ║
║   optimal throughput. Single agent would take ~29 min, 2 agents ~17 min.    ║
╚══════════════════════════════════════════════════════════════════════════════╝

Task decomposed into 5 subtasks. Recommended agents: 2. Estimated time: 25 min.
Use '/orchestrate' to execute this plan with multiple agents.

> /orchestrate

🚀 Starting orchestration with 2 agent(s)...
   Plan: Build a Calculator class with basic operations and comprehensive...
   Subtasks: 5
   Est. time: 25 minutes

  [agent-001] Starting task t1...
  [agent-001] ✓ Task t1 (8.2s)
  ── Phase 1/3 completed ──
  [agent-001] Starting task t2...
  [agent-002] Starting task t3...
  [agent-001] ✓ Task t2 (45.3s)
  [agent-002] ✓ Task t3 (52.1s)
  ── Phase 2/3 completed ──
  [agent-001] Starting task t4...
  [agent-002] Starting task t5...
  [agent-001] ✓ Task t4 (23.4s)
  [agent-002] ✓ Task t5 (28.7s)
  ── Phase 3/3 completed ──

╔══════════════════════════════════════════════════════════════════════════════╗
║ Orchestration Completed Successfully                                         ║
╠══════════════════════════════════════════════════════════════════════════════╣
║ Plan ID: a1b2c3d4                                                            ║
║ Duration: 2.6 minutes                                                        ║
║ Tasks: 5 completed, 0 failed                                                 ║
║ All changes merged successfully                                              ║
╠══════════════════════════════════════════════════════════════════════════════╣
║ Task Results:                                                                ║
║  [OK] t1 (agent-001) - 8.2s                                                  ║
║  [OK] t2 (agent-001) - 45.3s                                                 ║
║  [OK] t3 (agent-002) - 52.1s                                                 ║
║  [OK] t4 (agent-001) - 23.4s                                                 ║
║  [OK] t5 (agent-002) - 28.7s                                                 ║
╚══════════════════════════════════════════════════════════════════════════════╝

Orchestration completed successfully in 2.6 minutes.
```

## Error Handling

### Task Failures

When a subtask fails:
1. The agent is released back to the pool
2. Dependent tasks are marked as "Blocked"
3. Independent tasks continue executing
4. Final report shows which tasks failed and why

### Timeout Handling

- Each subtask has a configurable timeout (default: 30 min)
- Timed-out tasks are marked as failed
- The agent is forcefully released

### Cancellation

Press `Ctrl+C` or `Esc` during orchestration to:
1. Cancel all running tasks
2. Stop all agents gracefully
3. Report partial results

## Best Practices

1. **Start with `/plan`** - Always review the decomposition before executing
2. **Check dependencies** - Ensure the task graph makes sense
3. **Use recommended agents** - The system calculates optimal parallelization
4. **Review branches** - Use `--no-merge` for critical changes
5. **Monitor progress** - Watch the console for real-time updates

## Limitations

- Maximum 8 concurrent agents
- Agents share the same LLM endpoint (may bottleneck)
- Process isolation adds ~2-3 seconds startup overhead per agent
- Complex dependency cycles may cause deadlocks (auto-detected)

## Troubleshooting

### "No plan available"
Run `/plan <description>` before `/orchestrate`.

### "Pool is full"
All agents are busy. Wait for tasks to complete or reduce `--agents`.

### "Task timeout"
Increase `AgentTimeoutMinutes` in config or simplify the task.

### Merge conflicts
Use `--no-merge` and resolve conflicts manually, or ensure tasks work on different files.
