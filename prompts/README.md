# System Prompts Directory

This directory contains system prompt templates for T.H.U.V.U.

## Built-in Templates

The following templates are built into the application:

| Template | Description |
|----------|-------------|
| `coding` | Full-featured coding assistant with tool usage |
| `thinking` | For reasoning/thinking models (no tools) |
| `general` | General-purpose assistant |
| `minimal` | Bare-bones prompt |

## Custom Templates

Create `.md` files in this directory to add custom templates. They will be automatically discovered.

### Template Variables

Templates support variable substitution:

| Variable | Description |
|----------|-------------|
| `{{PLATFORM}}` | Platform info block (OS, shell, paths) |
| `{{OS}}` | Operating system description |
| `{{PATH_SEPARATOR}}` | Path separator character |
| `{{WORK_DIR}}` | Working directory path |
| `{{MODEL_ID}}` | Current model ID |
| `{{MODEL_NAME}}` | Model display name |
| `{{MAX_CONTEXT}}` | Max context length |
| `{{MAX_OUTPUT}}` | Max output tokens |
| `{{MCP_ENABLED}}` | Whether MCP is enabled |
| `{{TOOLS_ENABLED}}` | Whether model supports tools |
| `{{IS_THINKING_MODEL}}` | Whether it's a thinking model |

### Conditional Sections

Use `{{#CONDITION}}...{{/CONDITION}}` for conditional content:

```markdown
{{#MCP}}
This section only appears when MCP is enabled.
{{/MCP}}

{{#STANDARD}}
This section only appears when using standard tool calling.
{{/STANDARD}}
```

## Available Tools to Document

When creating prompts, consider documenting these tool categories:

### File Operations
- `read_file`, `write_file`, `search_files`, `apply_patch`

### Build & Test
- `dotnet_build`, `dotnet_test`, `dotnet_run`, `dotnet_new`, `dotnet_restore`

### Process Management
- `run_process` - blocking command execution
- `process_start` - start background process (returns session_id)
- `process_read` - read stdout/stderr from background process
- `process_write` - write to stdin of background process
- `process_status` - check process status or list all sessions
- `process_stop` - terminate background process

### UI Automation (requires permission)
- `ui_capture` - screenshot (analyze=true for vision model)
- `ui_list_windows` - enumerate windows
- `ui_focus_window` - bring window to foreground
- `ui_click` - mouse click
- `ui_type` - keyboard input
- `ui_mouse_move` - move cursor
- `ui_get_element` - inspect UI element
- `ui_wait` - wait for window/element

### Git & Version Control
- `git_status`, `git_diff`

### RAG (Semantic Search)
- `rag_index`, `rag_search`, `rag_stats`, `rag_clear`

### Code Indexing & Context (SQLite)
- `code_index` - index source files for symbol search
- `code_query` - search symbols by name, kind, file; find references
- `context_store` - store decisions, patterns, notes with categories
- `context_get` - retrieve context by key pattern or category
- `index_stats` - get index statistics
- `index_clear` - clear all indexed data

## Using Templates

### In appsettings.json

```json
{
  "Models": {
    "Models": [
      {
        "ModelId": "my-model",
        "SystemPromptTemplate": "coding"
      }
    ]
  }
}
```

### Custom Prompt per Model

```json
{
  "Models": {
    "Models": [
      {
        "ModelId": "my-model",
        "SystemPrompt": "You are a helpful assistant..."
      }
    ]
  }
}
```

### File Reference

```json
{
  "Models": {
    "Models": [
      {
        "ModelId": "my-model",
        "SystemPrompt": "@prompts/my-custom-prompt.md"
      }
    ]
  }
}
```
