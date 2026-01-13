# T.H.U.V.U. Configuration Guide

This document describes all configuration options available in `appsettings.json`.

## Configuration File Location

THUVU looks for configuration in the following order:
1. Environment variable `LM_AGENT_CONFIG` (if set)
2. `appsettings.json` in the executable directory
3. `appsettings.json` in the current working directory

## AgentConfig Section

Main agent configuration:

```json
{
  "AgentConfig": {
    "HostUrl": "http://127.0.0.1:1234",
    "Model": "qwen/qwen3-coder-30b",
    "Stream": true,
    "TimeoutMs": 1800000,
    "HttpRequestTimeout": 60,
    "WorkDirectory": "./work",
    "MaxContextLength": 130000,
    "MaxIterations": 50,
    "AutoApproveTuiTools": true,
    "ToolPermissions": {}
  }
}
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `HostUrl` | string | `http://127.0.0.1:1234` | LLM API endpoint |
| `Model` | string | (required) | Model ID to use |
| `Stream` | bool | `true` | Enable token streaming |
| `TimeoutMs` | int | `1800000` | Process timeout in milliseconds |
| `HttpRequestTimeout` | int | `60` | HTTP request timeout in minutes |
| `WorkDirectory` | string | `./work` | Working directory for operations |
| `MaxContextLength` | int | `0` | Max context tokens (0 = auto-detect) |
| `MaxIterations` | int | `50` | Max tool-calling iterations per request |
| `AutoApproveTuiTools` | bool | `true` | Auto-approve tools in TUI mode |
| `ToolPermissions` | object | `{}` | Persisted tool permissions |

## Models Section

Multi-model configuration with automatic selection:

```json
{
  "Models": {
    "DefaultModelId": "qwen/qwen3-coder-30b",
    "ThinkingModelId": "deepseek-reasoner",
    "CodingModelId": "deepseek-chat",
    "AutoSelectModel": true,
    "Models": [...]
  }
}
```

| Property | Type | Description |
|----------|------|-------------|
| `DefaultModelId` | string | Default model for general tasks |
| `ThinkingModelId` | string | Model for reasoning/planning tasks |
| `CodingModelId` | string | Model for code generation |
| `AutoSelectModel` | bool | Automatically select model based on task |
| `Models` | array | List of configured model endpoints |

### ModelEndpoint Configuration

Each model in the `Models` array:

```json
{
  "ModelId": "qwen/qwen3-coder-30b",
  "DisplayName": "Qwen3 Coder 30B",
  "HostUrl": "http://127.0.0.1:1234",
  "IsLocal": true,
  "Stream": true,
  "TimeoutMinutes": 60,
  "AuthScheme": "Bearer",
  "AuthToken": "",
  "AuthHeaderName": "Authorization",
  "MaxContextLength": 130000,
  "MaxOutputTokens": 8096,
  "Temperature": 0.2,
  "SupportsTools": true,
  "IsThinkingModel": false,
  "Purposes": ["Default", "Coding", "Review"],
  "Priority": 10,
  "Enabled": true,
  "SystemPrompt": "",
  "SystemPromptTemplate": "coding"
}
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `ModelId` | string | (required) | Unique model identifier |
| `DisplayName` | string | (ModelId) | Human-readable name |
| `HostUrl` | string | `http://127.0.0.1:1234` | API endpoint URL |
| `IsLocal` | bool | `true` | Whether model is local (affects timeouts) |
| `Stream` | bool | `true` | Enable streaming for this model |
| `TimeoutMinutes` | int | `60` | Request timeout in minutes |
| `AuthScheme` | string | `""` | Auth scheme (e.g., "Bearer") |
| `AuthToken` | string | `""` | API key or token |
| `AuthHeaderName` | string | `Authorization` | Header name for auth |
| `MaxContextLength` | int | `0` | Max context tokens (0 = auto) |
| `MaxOutputTokens` | int | `0` | Max output tokens (0 = default) |
| `Temperature` | double | `0.2` | Generation temperature |
| `SupportsTools` | bool | `true` | Whether model supports tool calling |
| `IsThinkingModel` | bool | `false` | Whether model shows reasoning |
| `Purposes` | array | `["Default"]` | Valid: Default, Coding, Review, Thinking |
| `Priority` | int | `0` | Selection priority (higher = preferred) |
| `Enabled` | bool | `true` | Whether model is available |
| `SystemPrompt` | string | `""` | Custom system prompt (or `@file` path) |
| `SystemPromptTemplate` | string | `""` | Template name (coding, thinking, etc.) |

### System Prompt Options

Models can have custom system prompts in three ways:

1. **Template reference**: `"SystemPromptTemplate": "coding"`
2. **File reference**: `"SystemPrompt": "@prompts/my-prompt.md"`
3. **Inline prompt**: `"SystemPrompt": "You are a helpful assistant..."`

Available built-in templates: `coding`, `thinking`, `general`, `minimal`

See [prompts/README.md](../prompts/README.md) for template documentation.

## RagConfig Section

RAG (Retrieval-Augmented Generation) configuration:

```json
{
  "RagConfig": {
    "ConnectionString": "Host=localhost;Port=5433;Database=thuvu_rag;Username=thuvu;Password=thuvu_secret",
    "EmbeddingDimension": 1536,
    "MaxChunkSize": 1000,
    "ChunkOverlap": 200,
    "TopK": 5,
    "SimilarityThreshold": 0.7,
    "Enabled": true,
    "EmbeddingHostUrl": "http://127.0.0.1:1234",
    "EmbeddingModel": "text-embedding-nomic-embed-text-v1.5"
  }
}
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `ConnectionString` | string | (required) | PostgreSQL connection string |
| `EmbeddingDimension` | int | `1536` | Vector embedding dimension |
| `MaxChunkSize` | int | `1000` | Max characters per chunk |
| `ChunkOverlap` | int | `200` | Overlap between chunks |
| `TopK` | int | `5` | Number of results to return |
| `SimilarityThreshold` | double | `0.7` | Minimum similarity score |
| `Enabled` | bool | `false` | Enable RAG features |
| `EmbeddingHostUrl` | string | (HostUrl) | Embedding API endpoint |
| `EmbeddingModel` | string | (required) | Embedding model name |

## McpConfig Section

MCP (Model Context Protocol) code execution configuration:

```json
{
  "McpConfig": {
    "Enabled": true,
    "DenoPath": "deno",
    "DefaultTimeout": 300000,
    "MaxMemoryMb": 512,
    "PermissionLevel": "readwrite",
    "SkillsDirectory": "./skills",
    "AuditLog": true,
    "RequireApproval": true
  }
}
```

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Enabled` | bool | `false` | Enable MCP code execution |
| `DenoPath` | string | `deno` | Path to Deno executable |
| `DefaultTimeout` | int | `300000` | Code execution timeout (ms) |
| `MaxMemoryMb` | int | `512` | Max memory for sandbox |
| `PermissionLevel` | string | `readwrite` | Options: readonly, readwrite, execute, full |
| `SkillsDirectory` | string | `./skills` | Directory for saved skills |
| `AuditLog` | bool | `true` | Log all code executions |
| `RequireApproval` | bool | `true` | Require user approval for code |

## Logging Section

Standard .NET logging configuration:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  }
}
```

## Environment Variables

Override configuration via environment:

| Variable | Description |
|----------|-------------|
| `LM_AGENT_CONFIG` | Path to appsettings.json |
| `THUVU_LLM_HOST` | Override LLM host URL |
| `THUVU_LLM_MODEL` | Override model ID |
| `THUVU_RAG_CONNECTION` | Override RAG connection string |
| `THUVU_EMBEDDING_HOST` | Override embedding host |
| `THUVU_EMBEDDING_MODEL` | Override embedding model |
| `THUVU_MCP_ENABLED` | Enable/disable MCP |
| `THUVU_WORK_DIR` | Override work directory |

## Example Configurations

### Local LM Studio Setup

```json
{
  "AgentConfig": {
    "HostUrl": "http://127.0.0.1:1234",
    "Model": "qwen/qwen3-coder-30b",
    "MaxContextLength": 130000
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
        "MaxContextLength": 130000
      }
    ]
  }
}
```

### DeepSeek Cloud Setup

```json
{
  "AgentConfig": {
    "HostUrl": "https://api.deepseek.com",
    "Model": "deepseek-chat"
  },
  "Models": {
    "DefaultModelId": "deepseek-chat",
    "ThinkingModelId": "deepseek-reasoner",
    "AutoSelectModel": true,
    "Models": [
      {
        "ModelId": "deepseek-chat",
        "DisplayName": "DeepSeek Chat",
        "HostUrl": "https://api.deepseek.com",
        "AuthToken": "sk-your-api-key",
        "IsLocal": false,
        "MaxContextLength": 130000,
        "SupportsTools": true,
        "SystemPromptTemplate": "deepseek"
      },
      {
        "ModelId": "deepseek-reasoner",
        "DisplayName": "DeepSeek Reasoner",
        "HostUrl": "https://api.deepseek.com",
        "AuthToken": "sk-your-api-key",
        "IsLocal": false,
        "MaxContextLength": 130000,
        "SupportsTools": false,
        "IsThinkingModel": true,
        "SystemPromptTemplate": "thinking"
      }
    ]
  }
}
```

### Hybrid Setup (Local + Cloud)

```json
{
  "AgentConfig": {
    "HostUrl": "http://127.0.0.1:1234",
    "Model": "qwen/qwen3-coder-30b"
  },
  "Models": {
    "DefaultModelId": "qwen/qwen3-coder-30b",
    "ThinkingModelId": "deepseek-reasoner",
    "AutoSelectModel": true,
    "Models": [
      {
        "ModelId": "qwen/qwen3-coder-30b",
        "DisplayName": "Qwen3 Coder (Local)",
        "HostUrl": "http://127.0.0.1:1234",
        "IsLocal": true,
        "SupportsTools": true,
        "Priority": 10
      },
      {
        "ModelId": "deepseek-reasoner",
        "DisplayName": "DeepSeek Reasoner (Cloud)",
        "HostUrl": "https://api.deepseek.com",
        "AuthToken": "sk-your-api-key",
        "IsLocal": false,
        "IsThinkingModel": true,
        "SupportsTools": false,
        "Priority": 20
      }
    ]
  }
}
```

### Docker Configuration

See [docker/appsettings.docker.json](../docker/appsettings.docker.json) for Docker-specific configuration with environment variable substitution.
