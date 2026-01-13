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
