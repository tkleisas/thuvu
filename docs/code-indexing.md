# SQLite Code Indexing & Context Storage

THUVU includes a local SQLite database for code indexing and context storage. This enables:

- **Code symbol search** - Find classes, methods, properties by name
- **Incremental indexing** - Only re-index changed files
- **Context memory** - Store decisions, patterns, notes for later retrieval
- **Zero dependencies** - Single file database, no external services

## Quick Start

```bash
# Index the current project
/sqlite index .

# Search for symbols
/sqlite query "MyClass"

# Store a context note
/sqlite store "api_pattern" "Use REST with JSON responses" --category pattern

# Get stored context
/sqlite get --category pattern
```

## Tools

### code_index
Index source code files for symbol search.

```json
{
  "path": "./src",       // Directory or file to index
  "force": false         // Re-index even if unchanged
}
```

**Example:**
```
Agent: code_index({"path": ".", "force": false})
Result: {"success": true, "totalFiles": 42, "indexedFiles": 5, "skippedFiles": 37}
```

### code_query
Search indexed code symbols.

```json
{
  "search": "Controller",     // Search pattern (partial match)
  "kind": "class",           // Filter: class, method, property, field, interface, enum
  "file": "./src/Api.cs",    // Get symbols in file or filter search
  "symbol_id": 123,          // Get specific symbol by ID
  "find_references": false,  // Find references to symbol_id
  "limit": 50                // Maximum results
}
```

**Examples:**
```
# Find all classes with "Service" in name
code_query({"search": "Service", "kind": "class"})

# Get all symbols in a file
code_query({"file": "Models/User.cs"})

# Find references to a symbol
code_query({"symbol_id": 42, "find_references": true})
```

### context_store
Store context/memory for later retrieval.

```json
{
  "key": "unique_key",           // Required: unique identifier
  "value": "content to store",   // Required: the content
  "category": "decision",        // Optional: decision, pattern, preference, note, error
  "project_path": "/path/to/project",  // Optional: associate with project
  "expires_in_days": 30          // Optional: auto-delete after N days (0=never)
}
```

**Examples:**
```
# Store a design decision
context_store({
  "key": "auth_approach",
  "value": "Using JWT tokens with refresh token rotation",
  "category": "decision"
})

# Store an error pattern to remember
context_store({
  "key": "null_ref_fix",
  "value": "Always null-check service injections in constructors",
  "category": "pattern"
})
```

### context_get
Retrieve stored context/memory.

```json
{
  "key_pattern": "auth",        // Search keys (partial match)
  "category": "decision",       // Filter by category
  "project_path": "/path",      // Filter by project
  "limit": 50                   // Maximum results
}
```

### index_stats
Get statistics about the code index.

```json
{}
```

Returns: total symbols, files, references, context entries, database size, symbols by kind.

### index_clear
Clear all indexed data. **Use with caution.**

```json
{}
```

## Indexed Symbol Types

The C# parser extracts:

| Kind | Description |
|------|-------------|
| `class` | Class declarations |
| `interface` | Interface declarations |
| `struct` | Struct declarations |
| `record` | Record declarations |
| `enum` | Enum declarations |
| `delegate` | Delegate declarations |
| `method` | Methods in classes/structs |
| `constructor` | Constructors |
| `property` | Properties |
| `field` | Fields |
| `event` | Events |
| `indexer` | Indexers |
| `enum_member` | Enum values |

## Configuration

In `appsettings.json`:

```json
{
  "SqliteConfig": {
    "Enabled": true,
    "DatabasePath": "",          // Empty = auto (work/thuvu.db)
    "IndexExtensions": [".cs", ".ts", ".js", ".py"],
    "ExcludeDirectories": ["bin", "obj", "node_modules", ".git"],
    "MaxFileSizeBytes": 1048576, // 1MB
    "ContextRetentionDays": 30   // Auto-cleanup old context
  }
}
```

## Database Location

By default, the database is created at:
- `{WorkDirectory}/thuvu.db` if work directory exists
- `{ExecutableDirectory}/thuvu.db` otherwise

Override with `DatabasePath` in config.

## Use Cases

### 1. Understanding a Codebase
```
> Index the project and find all controller classes

Agent: code_index({"path": "."})
Agent: code_query({"search": "Controller", "kind": "class"})
```

### 2. Finding Method Implementations
```
> Where is ProcessOrder implemented?

Agent: code_query({"search": "ProcessOrder", "kind": "method"})
```

### 3. Remembering Decisions
```
> Remember: we decided to use PostgreSQL for the main database

Agent: context_store({
  "key": "database_choice",
  "value": "PostgreSQL for main database - chosen for JSON support and full-text search",
  "category": "decision"
})
```

### 4. Recalling Context
```
> What decisions have we made about the database?

Agent: context_get({"key_pattern": "database", "category": "decision"})
```

### 5. Tracking Error Patterns
```
> Remember this null reference fix pattern

Agent: context_store({
  "key": "null_service_injection",
  "value": "When injecting services, use ?? throw new ArgumentNullException() in constructor",
  "category": "pattern"
})
```

## Future Enhancements

- [ ] TypeScript/JavaScript parsing
- [ ] Python parsing
- [ ] Reference tracking (where symbols are used)
- [ ] Cross-file analysis
- [ ] SQLite-based vector search (sqlite-vec)
