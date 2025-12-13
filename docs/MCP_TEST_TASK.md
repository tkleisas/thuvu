# MCP Test Task: Code Analysis Report

## Prerequisites

**Deno Runtime Required**: MCP code execution requires Deno to be installed.

### Install Deno

**Windows (PowerShell):**
```powershell
irm https://deno.land/install.ps1 | iex
```

**Windows (Chocolatey):**
```powershell
choco install deno
```

**macOS/Linux:**
```bash
curl -fsSL https://deno.land/install.sh | sh
```

After installation, verify with: `deno --version`

---

## Objective
Use MCP code execution to analyze the THUVU codebase and generate a report.

## Task Description

Create a TypeScript workflow that:

1. **Counts all files by extension** - Find all files in the project and group them by file extension
2. **Analyzes C# files** - Read the C# files and extract:
   - Total number of classes
   - Total number of methods (approximate by counting `public`, `private`, `protected` methods)
   - Files with the most lines of code
3. **Checks git status** - Get current git status
4. **Generates a summary report** - Return a structured JSON report

## Expected Output

```json
{
  "projectName": "thuvu",
  "analysis": {
    "totalFiles": 50,
    "filesByExtension": {
      "cs": 25,
      "ts": 15,
      "json": 5,
      "md": 5
    },
    "csharpAnalysis": {
      "totalClasses": 20,
      "totalMethods": 150,
      "largestFiles": [
        { "path": "Program.cs", "lines": 250 },
        { "path": "TuiInterface.cs", "lines": 500 }
      ]
    },
    "gitStatus": {
      "branch": "main",
      "modified": 3,
      "untracked": 1
    }
  },
  "generatedAt": "2025-12-13T17:52:00Z"
}
```

## How to Test

1. Start THUVU: `dotnet run`
2. Enable MCP: `/mcp enable`
3. Activate MCP mode: `/mcp on`
4. Enter the prompt:

```
Analyze this codebase using TypeScript code execution. Count all files by extension, analyze the C# files to find classes and methods, check git status, and generate a comprehensive JSON report.
```

5. The agent should respond with TypeScript code that gets executed
6. Review the results

## Alternative: Direct Code Execution

You can also test directly with `/mcp run`:

```
/mcp run "
import { searchFiles, readFile } from './servers/filesystem';
import { status } from './servers/git';

// Find all files
const allFiles = await searchFiles('**/*');

// Group by extension
const byExt = {};
for (const f of allFiles) {
  const ext = f.split('.').pop() || 'no-ext';
  byExt[ext] = (byExt[ext] || 0) + 1;
}

// Get C# files
const csFiles = await searchFiles('**/*.cs');

// Analyze first 10 C# files
const csAnalysis = [];
for (const f of csFiles.slice(0, 10)) {
  const content = await readFile(f);
  const lines = content.content.split('\n');
  const classes = lines.filter(l => l.includes('class ')).length;
  const methods = lines.filter(l => /\b(public|private|protected|internal)\b.*\(/.test(l)).length;
  csAnalysis.push({ path: f, lines: lines.length, classes, methods });
}

// Get git status
const gitStatus = await status();

return {
  totalFiles: allFiles.length,
  byExtension: byExt,
  csharpFiles: csFiles.length,
  topCsFiles: csAnalysis.sort((a, b) => b.lines - a.lines).slice(0, 5),
  gitStatus: gitStatus.stdout.split('\n').slice(0, 10)
};
"
```

## Success Criteria

- [ ] Code executes without errors
- [ ] File counts are accurate
- [ ] C# analysis returns meaningful data
- [ ] Git status is captured
- [ ] Results are returned as structured JSON
- [ ] Execution completes in under 30 seconds
