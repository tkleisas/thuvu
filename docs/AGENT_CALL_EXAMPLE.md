# THUVU Agent Call Examples

This document contains examples of commands sent to the THUVU coding agent during a test session to create a Mandelbrot console application.

## Session Overview

- **Date**: December 13, 2025
- **Models Tested**: `ibm/granite-4-h-tiny`, `qwen/qwen3-coder-30b`
- **Task**: Create a C# console application that renders the Mandelbrot set using ASCII characters

## Commands Sent to Agent

### 1. Setting the Model

```
/set model ibm/granite-4-h-tiny
```

```
/set model qwen/qwen3-coder-30b
```

### 2. Initial Creation Task

```
Create a C# console application that renders the Mandelbrot set using ASCII characters in C:\Users\tasos\Documents\projects\mandelbrot. Use write_file to create files. Requirements: 1) Display ASCII visualization using characters ' .:-=+*#%@' for iteration depths, 2) Zoom with +/- keys, 3) Pan with arrow keys, 4) ESC to quit.
```

### 3. Bug Fix Request

When the initial code had build errors, the following command was used to fix them:

```
Fix the build error in C:\Users\tasos\Documents\projects\mandelbrot\Program.cs - the MandelbrotIterations method has 'return 100 - i' but 'i' is declared inside the for loop. Declare 'int i;' before the loop and use 'for (i = 0; ...)'
```

### 4. Build Verification Request

```
The file is already fixed. Now build it with dotnet_build
```

## Agent Workflow Observed

### Successful Workflow (qwen/qwen3-coder-30b)

1. **Read File**: Agent reads the target file to understand current state
   ```
   [tool] read_file({"path":"C:\\Users\\tasos\\Documents\\projects\\mandelbrot\\Program.cs"})
   ```

2. **Apply Patch**: Agent generates and applies a unified diff patch
   ```
   [tool] apply_patch({"patch":"--- a/Program.cs\n+++ b/Program.cs\n@@ -54,10 +54,10 @@..."})
   ```

3. **Verify Changes**: Agent re-reads the file to confirm changes
   ```
   [tool] read_file({"path":"C:\\Users\\tasos\\Documents\\projects\\mandelbrot\\Program.cs"})
   ```

4. **Build**: Agent runs the build to verify compilation
   ```
   [tool] dotnet_build({"solution_or_project":"C:\\Users\\tasos\\Documents\\projects\\mandelbrot\\mandelbrot.csproj"})
   ```

### Project Creation Workflow

1. **Search for Existing Files**: Agent checks if project already exists
   ```
   [tool] search_files({"root":"C:\\Users\\tasos\\Documents\\projects","glob":"**/*.cs","query":"Mandelbrot"})
   ```

2. **Create Project**: Agent uses dotnet_new to scaffold
   ```
   [tool] dotnet_new({"solution_or_project":"C:\\Users\\tasos\\Documents\\projects\\mandelbrot","template":"console"})
   ```

3. **Write Code**: Agent writes the main program file
   ```
   [tool] write_file({"path":"C:\\Users\\tasos\\Documents\\projects\\mandelbrot\\Program.cs","content":"...","create_intermediate_dirs":true})
   ```

4. **Build and Verify**: Agent compiles the project
   ```
   [tool] dotnet_build({"solution_or_project":"C:\\Users\\tasos\\Documents\\projects\\mandelbrot\\mandelbrot.csproj"})
   ```

## Permission System

The agent prompts for permission before performing write operations:

```
⚠️  Permission Required
Tool 'write_file' wants to perform a write operation.
Arguments: {"path":"C:\\Users\\tasos\\Documents\\projects\\mandelbrot\\Program.cs",...}

Allow this operation?
  [A] Always for this repo
  [S] For this session
  [O] Once (this time only)
  [N] No (cancel)

Choice [A/S/O/N]:
```

Responses:
- `A` - Always allow for this repository
- `S` - Allow for this session only
- `O` - Allow once (this time only)
- `N` - Deny the operation

## Token Usage

The agent reports token usage after each LLM call:
```
[tokens] prompt=2563, completion=82, total=2645
```

## Lessons Learned

### Model Differences

| Model | Behavior |
|-------|----------|
| `ibm/granite-4-h-tiny` | Smaller model, got stuck in retry loops when encountering errors |
| `qwen/qwen3-coder-30b` | Larger model, correctly identified fixes and generated proper patches |

### Best Practices for Prompts

1. **Be specific about tools to use**: "Use write_file to create files"
2. **Provide exact paths**: Include full absolute paths when possible
3. **List requirements clearly**: Number them for clarity
4. **Include error context**: When fixing bugs, quote the exact error message

### Common Issues

1. **RAG tool misuse**: Smaller models sometimes try to use `rag_index` for creating files instead of `write_file`
2. **Patch line number mismatch**: LLMs may generate patches with incorrect line numbers (fixed with fuzzy matching)
3. **Variable scoping bugs**: Generated code may have C# scoping issues that require follow-up fixes

## Example Complete Session

```bash
# Start THUVU
dotnet run

# Set the model
>> /set model qwen/qwen3-coder-30b
Model set to: qwen/qwen3-coder-30b

# Submit task
>> Create a C# console application that renders the Mandelbrot set...

# Agent works through tools, prompting for permissions
# ...

# If build fails, submit fix request
>> Fix the build error in Program.cs - the variable 'i' is not accessible...

# Agent applies patch and rebuilds
# ...

# Exit when done
>> /exit
```
