using thuvu.Models;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace thuvu.Tools
{
    public  class BuildTools
    {
        public static List<Tool> GetBuildTools()
        {
            List<Tool> tools = new()
            {
            // --- Repo navigation ---
            new Tool
            {
                Type = "function",
                Function = new FunctionDef
                {
                    Name = "search_files",
                    Description = "Search files with a glob and optional content query.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "root":{"type":"string","description":"Start directory (absolute or relative). If omitted, auto-detect project root."},
                        "glob":{"type":"string","description":"Glob pattern, e.g. **/*.cs"},
                        "query":{"type":"string","description":"Case-insensitive substring to search inside files. If empty, just lists matches."},
                        "max_matches":{"type":"integer","minimum":1,"maximum":2000,"default":500}
                      },
                      "required":["glob"]
                    }
                    """).RootElement
                }
            },
            new Tool
            {
                Type = "function",
                Function = new FunctionDef
                {
                    Name = "read_file",
                    Description = "Read a file by absolute or relative path. Returns file content with SHA256 checksum. Supports reading specific line ranges for large files.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "path":{"type":"string","description":"Path to file (absolute or relative to work directory)"},
                        "start_line":{"type":"integer","minimum":1,"description":"First line to read (1-indexed). Use for large files."},
                        "end_line":{"type":"integer","minimum":1,"description":"Last line to read (1-indexed). Use for large files."},
                        "line_numbers":{"type":"boolean","default":false,"description":"If true, prefix each line with its line number"},
                        "max_lines":{"type":"integer","default":2000,"description":"Maximum lines to return if no range specified"}
                      },
                      "required":["path"]
                    }
                    """).RootElement
                }
            },

            // --- Safe edits ---
            new Tool
            {
                Type = "function",
                Function = new FunctionDef
                {
                    Name = "write_file",
                    Description = "Write an entire file. Returns new SHA256 checksum. Use expected_sha256 to prevent overwriting concurrent changes.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "path":{"type":"string","description":"Path to file (absolute or relative to work directory)"},
                        "content":{"type":"string","description":"Full file content to write"},
                        "expected_sha256":{"type":"string","description":"SHA256 from read_file to prevent clobbering. If file changed, write is rejected."},
                        "create_intermediate_dirs":{"type":"boolean","default":true,"description":"Create parent directories if they don't exist"},
                        "backup":{"type":"boolean","default":false,"description":"Create a .bak backup before overwriting"}
                      },
                      "required":["path","content"]
                    }
                    """).RootElement
                }
            },
            new Tool
            {
                Type = "function",
                Function = new FunctionDef
                {
                    Name = "apply_patch",
                    Description = "Apply a unified diff patch to the working tree.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "patch":{"type":"string","description":"Unified diff text"},
                        "root":{"type":"string","description":"Optional working directory for relative paths"}
                      },
                      "required":["patch"]
                    }
                    """).RootElement
                }
            },

            // --- Process runner (used by dotnet/git wrappers too) ---
            new Tool
            {
                Type = "function",
                Function = new FunctionDef
                {
                    Name = "run_process",
                    Description = "Run a whitelisted command with args.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "cmd":{"type":"string","enum":["dotnet","bash","powershell","git"]},
                        "args":{"type":"array","items":{"type":"string"}},
                        "cwd":{"type":"string"},
                        "timeout_ms":{"type":"integer","minimum":1000,"maximum":600000,"default":120000}
                      },
                      "required":["cmd"]
                    }
                    """).RootElement
                }
            },

            // --- dotnet helpers ---
            new Tool
            {
                Type = "function",
                Function = new FunctionDef
                {
                    Name = "dotnet_restore",
                    Description = "Run 'dotnet restore' for a solution or project.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "solution_or_project":{"type":"string","description":"Path to .sln or .csproj (optional)"}
                      }
                    }
                    """).RootElement
                }
            },
            new Tool
            {
                Type = "function",
                Function = new FunctionDef
                {
                    Name = "dotnet_build",
                    Description = "Run 'dotnet build'.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "solution_or_project":{"type":"string"},
                        "configuration":{"type":"string","enum":["Debug","Release"],"default":"Debug"},
                        "framework":{"type":"string","description":"e.g. net8.0"}
                      }
                    }
                    """).RootElement
                }
            },
            new Tool
            {
                Type = "function",
                Function = new FunctionDef
                {
                    Name = "dotnet_test",
                    Description = "Run 'dotnet test'.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "solution_or_project":{"type":"string"},
                        "filter":{"type":"string","description":"Test filter expression"},
                        "logger":{"type":"string","enum":["trx","console"],"default":"trx"}
                      }
                    }
                    """).RootElement
                }
            },
            new Tool
            {
                Type = "function",
                Function = new FunctionDef
                {
                    Name = "dotnet_run",
                    Description = "Run a .NET project. WARNING: GUI/game apps (MonoGame, WPF, etc.) will timeout as they don't exit automatically. Use for console apps only, or set a short timeout.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "solution_or_project":{"type":"string","description":"Path to project file or directory"},
                        "configuration":{"type":"string","description":"Build configuration (Debug/Release)"},
                        "no_build":{"type":"boolean","default":false,"description":"Skip build before running"},
                        "args":{"type":"array","items":{"type":"string"},"description":"Arguments to pass to the application"},
                        "timeout_ms":{"type":"integer","default":30000,"description":"Timeout in ms. Default 30s. GUI apps will always timeout."}
                      }
                    }
                    """).RootElement
                }
            },
            new Tool
            {
                Type = "function",
                Function = new FunctionDef
                {
                    Name = "dotnet_new",
                    Description = "Create a new .NET project from template.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "solution_or_project":{"type":"string"},
                        "template":{"type":"string","description":"Select template for project"}
                      }
                    }
                    """).RootElement
                }
            },
            // --- git helpers ---
            new Tool
            {
                Type = "function",
                Function = new FunctionDef
                {
                    Name = "git_status",
                    Description = "Get branch and working tree status (porcelain).",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "root":{"type":"string","description":"Working directory (optional)"}
                      }
                    }
                    """).RootElement
                }
            },
            new Tool
            {
                Type = "function",
                Function = new FunctionDef
                {
                    Name = "git_diff",
                    Description = "Get a diff for paths (unstaged by default).",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "paths":{"type":"array","items":{"type":"string"}},
                        "staged":{"type":"boolean","default":false},
                        "context":{"type":"integer","minimum":0,"maximum":100,"default":3}
                      }
                    }
                    """).RootElement
                }
            },

            // --- NuGet helpers (optional but handy) ---
            new Tool
            {
                Type = "function",
                Function = new FunctionDef
                {
                    Name = "nuget_search",
                    Description = "Search NuGet packages by query.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "query":{"type":"string"}
                      },
                      "required":["query"]
                    }
                    """).RootElement
                }
            },
            new Tool
            {
                Type = "function",
                Function = new FunctionDef
                {
                    Name = "nuget_add",
                    Description = "Add a NuGet package to a project.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "id":{"type":"string"},
                        "version":{"type":"string"},
                        "project":{"type":"string","description":"Path to .csproj (optional)"}
                      },
                      "required":["id"]
                    }
                    """).RootElement
                }
            },

            // --- RAG tools ---
            new Tool
            {
                Type = "function",
                Function = new FunctionDef
                {
                    Name = "rag_index",
                    Description = "Index EXISTING files or directories for RAG search. Only use on files that already exist. For creating new files, use write_file instead.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "path":{"type":"string","description":"Path to existing file or directory to index"},
                        "recursive":{"type":"boolean","default":false,"description":"If path is a directory, index recursively"},
                        "pattern":{"type":"string","default":"*.cs","description":"Simple file pattern like *.cs (not glob patterns like **/*.cs)"}
                      },
                      "required":["path"]
                    }
                    """).RootElement
                }
            },
            new Tool
            {
                Type = "function",
                Function = new FunctionDef
                {
                    Name = "rag_search",
                    Description = "Search the RAG index for content semantically similar to the query.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "query":{"type":"string","description":"Search query text"},
                        "top_k":{"type":"integer","minimum":1,"maximum":50,"default":5,"description":"Number of results to return"}
                      },
                      "required":["query"]
                    }
                    """).RootElement
                }
            },
            new Tool
            {
                Type = "function",
                Function = new FunctionDef
                {
                    Name = "rag_clear",
                    Description = "Clear the RAG index. Can clear all documents or just documents from a specific source.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "source_path":{"type":"string","description":"Optional: only clear documents from this source path"}
                      }
                    }
                    """).RootElement
                }
            },
            new Tool
            {
                Type = "function",
                Function = new FunctionDef
                {
                    Name = "rag_stats",
                    Description = "Get statistics about the RAG index (total chunks, sources, characters).",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{}
                    }
                    """).RootElement
                }
            },

            // --- Code execution (MCP/TypeScript sandbox) ---
            new Tool
            {
                Type = "function",
                Function = new FunctionDef
                {
                    Name = "execute_code",
                    Description = @"Execute TypeScript code in a sandboxed Deno environment. 

WHEN TO USE: Use for complex batch operations that would require many sequential tool calls, like:
- Reading and processing 5+ files together
- Complex file transformations
- Data analysis across multiple files

WHEN NOT TO USE: Prefer native tools for simple operations:
- Use read_file for reading 1-3 files
- Use write_file for writing files
- Use search_files for searching
- Use dotnet_build/dotnet_test for .NET operations

Available functions (auto-imported):
- searchFiles(glob: string, query?: string): Promise<string[]>
- readFile(path: string): Promise<{content: string, sha256: string}>
- writeFile(path: string, content: string, expectedSha256?: string): Promise<{success: boolean}>
- applyPatch(patch: string, root?: string): Promise<{applied: boolean}>
- runCommand(cmd: string, args: string[]): Promise<{stdout: string, stderr: string, exitCode: number}>
- git(args: string[]): Promise<{stdout: string, stderr: string, exitCode: number}>
- dotnet(args: string[]): Promise<{stdout: string, stderr: string, exitCode: number}>

NOTE: Avoid running long commands like 'dotnet build' inside execute_code - use native tools instead.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "code":{"type":"string","description":"TypeScript code to execute. Must return a value to get results."},
                        "timeout_ms":{"type":"integer","minimum":1000,"maximum":600000,"description":"Execution timeout in milliseconds. Default from McpConfig.DefaultTimeout (5 minutes)."}
                      },
                      "required":["code"]
                    }
                    """).RootElement
                }
            },

            // --- Browser/Web tools (Playwright) ---
            new Tool
            {
                Type = "function",
                Function = new FunctionDef
                {
                    Name = "browser_navigate",
                    Description = "Navigate to a URL and get page content. Use for web research, reading documentation, or testing web applications.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "url":{"type":"string","description":"URL to navigate to"},
                        "extract_text":{"type":"boolean","default":true,"description":"If true, extracts clean text. If false, returns HTML."},
                        "screenshot":{"type":"boolean","default":false,"description":"Take a screenshot of the page"},
                        "wait_for":{"type":"string","description":"CSS selector to wait for before returning"},
                        "timeout_ms":{"type":"integer","minimum":1000,"maximum":60000,"default":30000}
                      },
                      "required":["url"]
                    }
                    """).RootElement
                }
            },
            new Tool
            {
                Type = "function",
                Function = new FunctionDef
                {
                    Name = "browser_click",
                    Description = "Click an element on the current page. Requires browser_navigate to be called first.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "selector":{"type":"string","description":"CSS selector of element to click"},
                        "timeout_ms":{"type":"integer","minimum":1000,"maximum":30000,"default":10000}
                      },
                      "required":["selector"]
                    }
                    """).RootElement
                }
            },
            new Tool
            {
                Type = "function",
                Function = new FunctionDef
                {
                    Name = "browser_type",
                    Description = "Type text into an input element. Requires browser_navigate to be called first.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "selector":{"type":"string","description":"CSS selector of input element"},
                        "text":{"type":"string","description":"Text to type"},
                        "clear":{"type":"boolean","default":true,"description":"Clear existing text before typing"},
                        "press_enter":{"type":"boolean","default":false,"description":"Press Enter after typing"}
                      },
                      "required":["selector","text"]
                    }
                    """).RootElement
                }
            },
            new Tool
            {
                Type = "function",
                Function = new FunctionDef
                {
                    Name = "browser_get_elements",
                    Description = "Get elements matching a CSS selector. Useful for finding links, buttons, or form fields.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "selector":{"type":"string","description":"CSS selector to match"},
                        "max":{"type":"integer","minimum":1,"maximum":100,"default":20,"description":"Maximum elements to return"}
                      },
                      "required":["selector"]
                    }
                    """).RootElement
                }
            },
            new Tool
            {
                Type = "function",
                Function = new FunctionDef
                {
                    Name = "browser_screenshot",
                    Description = "Take a screenshot of the current page or a specific element.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "full_page":{"type":"boolean","default":false,"description":"Capture the full scrollable page"},
                        "selector":{"type":"string","description":"CSS selector of element to screenshot (optional)"}
                      }
                    }
                    """).RootElement
                }
            },
            new Tool
            {
                Type = "function",
                Function = new FunctionDef
                {
                    Name = "browser_script",
                    Description = "Execute JavaScript on the current page. Use for advanced interactions or data extraction.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "script":{"type":"string","description":"JavaScript code to execute. Use 'return' to get a result."}
                      },
                      "required":["script"]
                    }
                    """).RootElement
                }
            },
            new Tool
            {
                Type = "function",
                Function = new FunctionDef
                {
                    Name = "browser_close",
                    Description = "Close the browser. Call when done with web browsing to free resources.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{}
                    }
                    """).RootElement
                }
            },
            
            // --- Vision/Image Analysis ---
            new Tool
            {
                Type = "function",
                Function = new FunctionDef
                {
                    Name = "analyze_image",
                    Description = @"Analyze an image using a vision-capable LLM. Use to:
- Verify UI/screenshot appearance
- Check if visual elements are displayed correctly
- Get textual descriptions of images or screenshots
- Validate app visual output

The image can be provided as a file path or base64 data.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "image_path":{"type":"string","description":"Path to image file (absolute or relative to work directory)"},
                        "image_base64":{"type":"string","description":"Base64-encoded image data (alternative to image_path)"},
                        "prompt":{"type":"string","description":"What to analyze or check in the image. Default: 'Describe this image in detail.'"}
                      }
                    }
                    """).RootElement
                }
            }
        };

            return tools;
        }

    }
}
