# Process Management Tools

## Overview

The process management tools allow the agent to start, monitor, and interact with long-running background processes. This is particularly useful for:

- Running web servers or GUI applications that need to stay alive
- Debugging applications while observing their output
- Interacting with interactive command-line tools
- Combining with UI automation for visual debugging workflows

## Tools

### `process_start`

Start a background process that continues running independently.

**Parameters:**
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `cmd` | string | Yes | Command to run (must be in allowed list) |
| `args` | string[] | No | Command arguments |
| `cwd` | string | No | Working directory (defaults to project directory) |

**Returns:**
```json
{
  "success": true,
  "session_id": "proc_0001_143522",
  "pid": 12345,
  "command": "dotnet",
  "arguments": ["run"],
  "working_directory": "C:\\project",
  "started_at": "2025-01-15T14:35:22.123Z"
}
```

### `process_read`

Read stdout/stderr output from a background process.

**Parameters:**
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `session_id` | string | Yes | Session ID from `process_start` |
| `all` | boolean | No | If true, read all output from beginning (default: only new output) |
| `wait_ms` | integer | No | Wait this many ms before reading (max 30000) |

**Returns:**
```json
{
  "success": true,
  "session_id": "proc_0001_143522",
  "is_running": true,
  "exit_code": null,
  "stdout": "Application started on http://localhost:5000\n",
  "stderr": ""
}
```

### `process_write`

Write input to a background process's stdin.

**Parameters:**
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `session_id` | string | Yes | Session ID from `process_start` |
| `input` | string | Yes | Text to send to the process |
| `no_newline` | boolean | No | If true, don't append newline after input |

**Returns:**
```json
{
  "success": true,
  "session_id": "proc_0001_143522",
  "bytes_written": 6
}
```

### `process_status`

Get status of a specific session or list all active sessions.

**Parameters:**
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `session_id` | string | No | Session ID to check (omit to list all sessions) |

**Returns (single session):**
```json
{
  "success": true,
  "session_id": "proc_0001_143522",
  "pid": 12345,
  "command": "dotnet",
  "arguments": ["run"],
  "working_directory": "C:\\project",
  "is_running": true,
  "exit_code": null,
  "started_at": "2025-01-15T14:35:22.123Z",
  "runtime_seconds": 45.5
}
```

**Returns (list all):**
```json
{
  "success": true,
  "session_count": 2,
  "sessions": [
    {
      "session_id": "proc_0001_143522",
      "pid": 12345,
      "command": "dotnet",
      "is_running": true,
      "exit_code": null,
      "started_at": "2025-01-15T14:35:22.123Z",
      "runtime_seconds": 45.5
    }
  ]
}
```

### `process_stop`

Stop a background process and remove its session.

**Parameters:**
| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `session_id` | string | Yes | Session ID to stop |
| `force` | boolean | No | If true, force kill immediately without graceful shutdown |

**Returns:**
```json
{
  "success": true,
  "session_id": "proc_0001_143522",
  "exit_code": 0,
  "final_stdout": "Application shutting down...\n",
  "final_stderr": "",
  "message": "Process stopped and session removed"
}
```

## Allowed Commands

The same command whitelist as `run_process` applies:
- Build tools: `dotnet`, `npm`, `npx`, `node`, `yarn`, `pnpm`, `cargo`, `rustc`, `go`, `python`, `python3`, `pip`, `pip3`, `make`, `cmake`, `gradle`, `mvn`
- Version control: `git`, `svn`
- Shell: `bash`, `sh`, `powershell`, `pwsh`, `cmd`
- Utilities: `cat`, `head`, `tail`, `grep`, `find`, `ls`, `dir`, `tree`, `curl`, `wget`

## Permissions

- `process_start`, `process_write`, `process_stop`: Write permission required
- `process_status`, `process_read`: Read-only permission (allowed after first write permission grant)

## Example Workflows

### 1. Debug a Web Application

```
1. process_start: cmd="dotnet", args=["run", "--project", "WebApp"]
   → Returns session_id="proc_0001"

2. process_read: session_id="proc_0001", wait_ms=5000
   → Shows "Now listening on: http://localhost:5000"

3. ui_capture: analyze=true, analyze_prompt="Check the web page at localhost:5000"
   → Vision model describes the page

4. ui_click: x=300, y=200  (click a button)

5. process_read: session_id="proc_0001"
   → Shows any console output from the click

6. process_stop: session_id="proc_0001"
   → Terminate when done
```

### 2. Debug a WPF/WinForms Application

```
1. process_start: cmd="dotnet", args=["run", "--project", "MyWpfApp"]
   → Application window opens

2. ui_wait: window_title="My App"
   → Wait for window to appear

3. ui_capture: window_title="My App", analyze=true
   → See current state

4. ui_type: text="test@example.com", window_title="My App"
   → Fill in a text field

5. ui_click: x=500, y=400  (click Submit)

6. process_read: session_id="proc_0001"
   → Check for validation errors in console

7. ui_capture: analyze=true, analyze_prompt="What error messages are shown?"
   → Analyze the result

8. process_stop: session_id="proc_0001"
```

### 3. Interactive CLI Tool

```
1. process_start: cmd="npm", args=["init"]
   → Starts interactive npm init

2. process_read: session_id="proc_0001", wait_ms=1000
   → Shows "package name: (project)"

3. process_write: session_id="proc_0001", input="my-project"
   → Answer the prompt

4. process_read: session_id="proc_0001", wait_ms=500
   → Shows next prompt

... continue answering prompts ...

5. process_stop: session_id="proc_0001"
```

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                     ProcessSessionManager                    │
│                        (Singleton)                           │
├─────────────────────────────────────────────────────────────┤
│  ConcurrentDictionary<string, ProcessSession>                │
│  - StartProcess() → creates and starts session               │
│  - GetSession() → retrieve by ID                             │
│  - ListSessions() → all active                               │
│  - StopSession() → terminate and remove                      │
│  - CleanupExitedSessions() → garbage collect                 │
└─────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────┐
│                      ProcessSession                          │
├─────────────────────────────────────────────────────────────┤
│  - SessionId, ProcessId, Command, Arguments                  │
│  - Start() → launch the process                              │
│  - ReadOutput() → new output since last read                 │
│  - ReadAllOutput() → complete output history                 │
│  - WriteInput() / WriteLineInput() → send to stdin           │
│  - Stop() → graceful or force termination                    │
│  - IsRunning, ExitCode properties                            │
└─────────────────────────────────────────────────────────────┘
```

## Comparison with `run_process`

| Feature | `run_process` | `process_start` + tools |
|---------|---------------|-------------------------|
| Blocking | Yes (waits for completion) | No (returns immediately) |
| Timeout | Yes (default 120s, max 600s) | No timeout |
| Interactive | No | Yes (can write to stdin) |
| Output access | All at once when done | Incremental reads |
| Multiple processes | Sequential only | Parallel sessions |
| Use case | Quick commands | Long-running apps |

## Testing

Run the process management test suite:
```bash
dotnet run -- --test-process
```
