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
                    Description = "Write an entire file. Returns new SHA256 checksum. Use expected_sha256 to prevent overwriting concurrent changes. WARNING: For files larger than 6KB, use write_file_chunk instead to avoid output truncation issues.",
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
                    Name = "write_file_chunk",
                    Description = "Write a large file in chunks to avoid output truncation. IMPORTANT: Each chunk must be under 4KB (~100 lines of code). Send chunks in order (1, 2, 3...) - the file is written atomically when the final chunk is received. Example: 200-line file = 2 chunks of 100 lines each.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "path":{"type":"string","description":"Path to file (absolute or relative to work directory)"},
                        "content":{"type":"string","description":"Chunk content - MUST be under 4KB (~100 lines). Split larger content into multiple chunks."},
                        "chunk_number":{"type":"integer","minimum":1,"description":"Current chunk number (1-indexed)"},
                        "total_chunks":{"type":"integer","minimum":1,"description":"Total number of chunks. Calculate as: ceil(total_lines / 100)"},
                        "expected_sha256":{"type":"string","description":"SHA256 of existing file (only checked on first chunk)"}
                      },
                      "required":["path","content","chunk_number","total_chunks"]
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
                    Description = "Apply a unified diff patch. IMPORTANT: You MUST read_file first before creating a patch — never guess file content. If the patch fails, re-read the file and try again.",
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
            },
            
            // --- UI Automation tools ---
            new Tool
            {
                Type = "function",
                Function = new FunctionDef
                {
                    Name = "ui_capture",
                    Description = @"Capture a screenshot of the screen, a window, or a region.
Use for visual debugging, verifying UI state, or feeding images to vision models.

Modes:
- fullscreen: Capture entire screen (all monitors)
- window: Capture specific window by title
- region: Capture rectangular area by coordinates

Set analyze=true to automatically analyze the image with the vision model and get a description.
Returns base64-encoded image data by default, or saves to file.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "mode":{"type":"string","enum":["fullscreen","window","region"],"default":"fullscreen","description":"Capture mode"},
                        "window_title":{"type":"string","description":"Window title for mode=window (partial match supported)"},
                        "x":{"type":"integer","description":"Left coordinate for mode=region"},
                        "y":{"type":"integer","description":"Top coordinate for mode=region"},
                        "width":{"type":"integer","description":"Width in pixels for mode=region"},
                        "height":{"type":"integer","description":"Height in pixels for mode=region"},
                        "format":{"type":"string","enum":["png","jpeg"],"default":"png","description":"Image format"},
                        "quality":{"type":"integer","minimum":1,"maximum":100,"default":"85","description":"JPEG quality (ignored for PNG)"},
                        "output":{"type":"string","enum":["base64","file"],"default":"base64","description":"Output mode"},
                        "file_path":{"type":"string","description":"File path when output=file"},
                        "include_cursor":{"type":"boolean","default":false,"description":"Include mouse cursor in capture"},
                        "analyze":{"type":"boolean","default":false,"description":"If true, analyze the captured image with the vision model and return a description"},
                        "analyze_prompt":{"type":"string","description":"Custom prompt for image analysis (used when analyze=true). Default: 'Describe what you see in this screenshot.'"}
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
                    Name = "ui_list_windows",
                    Description = "List all open windows. Use to discover window titles for ui_capture, ui_click, or ui_focus_window.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "include_hidden":{"type":"boolean","default":false,"description":"Include hidden/background windows"},
                        "filter":{"type":"string","description":"Filter by title (case-insensitive partial match)"}
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
                    Name = "ui_focus_window",
                    Description = "Bring a window to the foreground and give it focus. Use before ui_click or ui_type to target a specific window.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "window_title":{"type":"string","description":"Window title (partial match supported)"}
                      },
                      "required":["window_title"]
                    }
                    """).RootElement
                }
            },
            new Tool
            {
                Type = "function",
                Function = new FunctionDef
                {
                    Name = "ui_click",
                    Description = "Click at screen coordinates or coordinates relative to a window. Use after ui_capture to interact with UI elements.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "x":{"type":"integer","description":"X coordinate (screen or window-relative)"},
                        "y":{"type":"integer","description":"Y coordinate (screen or window-relative)"},
                        "button":{"type":"string","enum":["left","right","middle"],"default":"left","description":"Mouse button"},
                        "clicks":{"type":"integer","enum":[1,2],"default":1,"description":"1=single click, 2=double click"},
                        "window_title":{"type":"string","description":"If provided, coordinates are relative to this window"}
                      },
                      "required":["x","y"]
                    }
                    """).RootElement
                }
            },
            new Tool
            {
                Type = "function",
                Function = new FunctionDef
                {
                    Name = "ui_type",
                    Description = @"Type text or send keyboard input to the active window or a specific window.

For regular text input (forms, text editors), use the 'text' parameter.
For special keys/shortcuts, use the 'keys' array: ['ctrl', 's'], ['alt', 'f4'], ['enter'], ['left'], ['right'], etc.

For GAMES that don't respond to normal keyboard input:
- Set 'use_scan_codes' to true - this sends hardware scan codes that games using DirectInput/RawInput can detect
- Adjust 'hold_time_ms' for how long each key is held (games often need longer holds, e.g., 100ms)
- Adjust 'delay_ms' for delay between keys in a sequence

Supported keys: ctrl, alt, shift, win, enter, tab, escape, space, backspace, delete, insert, home, end, 
pageup, pagedown, left, right, up, down, f1-f12, numpad0-9, capslock, numlock, pause, printscreen, 
and single characters/numbers (a-z, 0-9).",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "text":{"type":"string","description":"Text to type literally (for text fields, not games)"},
                        "keys":{"type":"array","items":{"type":"string"},"description":"Keys to send, e.g. ['left'], ['ctrl','s'], ['space']"},
                        "window_title":{"type":"string","description":"Target window (will focus first)"},
                        "delay_ms":{"type":"integer","minimum":0,"maximum":1000,"default":10,"description":"Delay between keys in ms"},
                        "use_scan_codes":{"type":"boolean","default":false,"description":"Use hardware scan codes for games using DirectInput/RawInput"},
                        "hold_time_ms":{"type":"integer","minimum":10,"maximum":500,"default":50,"description":"How long to hold each key in ms (for games)"}
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
                    Name = "ui_mouse_move",
                    Description = "Move the mouse cursor to specified coordinates without clicking.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "x":{"type":"integer","description":"X coordinate"},
                        "y":{"type":"integer","description":"Y coordinate"},
                        "window_title":{"type":"string","description":"If provided, coordinates are relative to this window"}
                      },
                      "required":["x","y"]
                    }
                    """).RootElement
                }
            },
            // ui_get_element - Get UI element information
            new Tool
            {
                Type = "function",
                Function = new FunctionDef
                {
                    Name = "ui_get_element",
                    Description = "Get UI element at coordinates or find elements by selector. Returns element properties including automation ID, name, type, bounds, and enabled/visible state. Useful for UI automation debugging.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "x":{"type":"integer","description":"X coordinate to get element at (screen coordinates)"},
                        "y":{"type":"integer","description":"Y coordinate to get element at (screen coordinates)"},
                        "selector":{"type":"string","description":"Element selector in format 'ControlType:Name' (e.g., 'Button:Submit', 'TextBox:*', 'Edit:Username')"},
                        "window_title":{"type":"string","description":"Limit element search to this window (optional)"}
                      }
                    }
                    """).RootElement
                }
            },
            // ui_wait - Wait for window or element to appear
            new Tool
            {
                Type = "function",
                Function = new FunctionDef
                {
                    Name = "ui_wait",
                    Description = "Wait for a window or UI element to appear. Useful for waiting for dialogs, loading indicators, or dynamic UI elements.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "window_title":{"type":"string","description":"Window title pattern to wait for (supports substring match)"},
                        "element_selector":{"type":"string","description":"Element selector to wait for (format: 'ControlType:Name')"},
                        "timeout_ms":{"type":"integer","description":"Maximum time to wait in milliseconds (default: 10000)"}
                      }
                    }
                    """).RootElement
                }
            },

            // --- Process Management Tools ---
            new Tool
            {
                Type = "function",
                Function = new FunctionDef
                {
                    Name = "process_start",
                    Description = "Start a background process that continues running. Returns a session_id for later interaction. Use for long-running apps, servers, GUI applications. The process runs independently and can be interacted with via process_read, process_write, process_status, and process_stop.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "cmd":{"type":"string","description":"Command to run (must be in allowed list: dotnet, npm, node, python, etc.)"},
                        "args":{"type":"array","items":{"type":"string"},"description":"Command arguments"},
                        "cwd":{"type":"string","description":"Working directory (defaults to project directory)"}
                      },
                      "required":["cmd"]
                    }
                    """).RootElement
                }
            },
            new Tool
            {
                Type = "function",
                Function = new FunctionDef
                {
                    Name = "process_read",
                    Description = "Read stdout/stderr output from a background process. By default reads only new output since last read. Use 'all' to read complete output from start.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "session_id":{"type":"string","description":"Session ID from process_start"},
                        "all":{"type":"boolean","description":"If true, read all output from beginning instead of just new output"},
                        "wait_ms":{"type":"integer","description":"Wait this many milliseconds before reading (useful when expecting output, max 30000)"}
                      },
                      "required":["session_id"]
                    }
                    """).RootElement
                }
            },
            new Tool
            {
                Type = "function",
                Function = new FunctionDef
                {
                    Name = "process_write",
                    Description = "Write input to a background process stdin. Useful for interactive applications that expect user input.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "session_id":{"type":"string","description":"Session ID from process_start"},
                        "input":{"type":"string","description":"Text to send to the process"},
                        "no_newline":{"type":"boolean","description":"If true, don't append newline after input"}
                      },
                      "required":["session_id","input"]
                    }
                    """).RootElement
                }
            },
            new Tool
            {
                Type = "function",
                Function = new FunctionDef
                {
                    Name = "process_status",
                    Description = "Get status of a background process or list all running sessions. Without session_id, lists all sessions.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "session_id":{"type":"string","description":"Session ID to check (optional - omit to list all sessions)"}
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
                    Name = "process_stop",
                    Description = "Stop a background process and remove its session. Returns final output before termination.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "session_id":{"type":"string","description":"Session ID to stop"},
                        "force":{"type":"boolean","description":"If true, force kill immediately without graceful shutdown"}
                      },
                      "required":["session_id"]
                    }
                    """).RootElement
                }
            },

            // --- Code Indexing & Context Tools ---
            new Tool
            {
                Type = "function",
                Function = new FunctionDef
                {
                    Name = "code_index",
                    Description = "Index source code files for symbol search. Extracts classes, methods, properties, fields, etc. Supports incremental indexing (only changed files). Use to enable code_query searches.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "path":{"type":"string","description":"Directory or file to index. Indexes recursively for directories."},
                        "force":{"type":"boolean","default":false,"description":"Re-index even if file unchanged"}
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
                    Name = "code_query",
                    Description = "Query indexed code symbols. Search for classes, methods, properties by name. Get symbols in a file. Find references to a symbol.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "search":{"type":"string","description":"Search pattern for symbol names (partial match)"},
                        "kind":{"type":"string","enum":["class","interface","struct","enum","method","property","field","constructor"],"description":"Filter by symbol kind"},
                        "file":{"type":"string","description":"Get all symbols in this file, or filter search to this file"},
                        "symbol_id":{"type":"integer","description":"Get specific symbol by ID"},
                        "find_references":{"type":"boolean","default":false,"description":"Find all references to the symbol (requires symbol_id)"},
                        "limit":{"type":"integer","default":50,"description":"Maximum results to return"}
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
                    Name = "context_store",
                    Description = "Store context/memory for later retrieval. Use to remember decisions, patterns, errors, or any information across sessions.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "key":{"type":"string","description":"Unique key for this context entry"},
                        "value":{"type":"string","description":"The content to store"},
                        "category":{"type":"string","enum":["decision","pattern","preference","note","error"],"description":"Category for filtering"},
                        "project_path":{"type":"string","description":"Associate with a specific project directory"},
                        "expires_in_days":{"type":"integer","description":"Auto-delete after N days (default: 30, 0=never)"}
                      },
                      "required":["key","value"]
                    }
                    """).RootElement
                }
            },
            new Tool
            {
                Type = "function",
                Function = new FunctionDef
                {
                    Name = "context_get",
                    Description = "Retrieve stored context/memory. Search by key pattern, category, or project.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "key_pattern":{"type":"string","description":"Search pattern for keys (partial match)"},
                        "category":{"type":"string","enum":["decision","pattern","preference","note","error"],"description":"Filter by category"},
                        "project_path":{"type":"string","description":"Filter by project directory"},
                        "limit":{"type":"integer","default":50,"description":"Maximum results"}
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
                    Name = "index_stats",
                    Description = "Get statistics about the code index: total symbols, files, references, database size.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{}
                    }
                    """).RootElement
                }
            },
            new Tool
            {
                Type = "function",
                Function = new FunctionDef
                {
                    Name = "index_clear",
                    Description = "Clear all indexed data (symbols, files, context). Use with caution.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{}
                    }
                    """).RootElement
                }
            },

            // --- Agent Communication Tools ---
            new Tool
            {
                Type = "function",
                Function = new FunctionDef
                {
                    Name = "agent_list",
                    Description = "List all known agents and their current status (idle/busy/offline).",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{}
                    }
                    """).RootElement
                }
            },
            new Tool
            {
                Type = "function",
                Function = new FunctionDef
                {
                    Name = "agent_submit",
                    Description = "Submit a prompt/task to another agent. Returns immediately with a job ID. Agent must not be busy.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "agent_name":{"type":"string","description":"Name of the target agent (from agent_list)"},
                        "prompt":{"type":"string","description":"The task/prompt to send to the agent"}
                      },
                      "required":["agent_name","prompt"]
                    }
                    """).RootElement
                }
            },
            new Tool
            {
                Type = "function",
                Function = new FunctionDef
                {
                    Name = "agent_status",
                    Description = "Get the current job status and journal from an agent. Use to poll progress.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "agent_name":{"type":"string","description":"Name of the target agent"}
                      },
                      "required":["agent_name"]
                    }
                    """).RootElement
                }
            },
            new Tool
            {
                Type = "function",
                Function = new FunctionDef
                {
                    Name = "agent_result",
                    Description = "Get the full result of a completed job from an agent.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "agent_name":{"type":"string","description":"Name of the target agent"},
                        "job_id":{"type":"string","description":"The job ID returned from agent_submit"}
                      },
                      "required":["agent_name","job_id"]
                    }
                    """).RootElement
                }
            },
            new Tool
            {
                Type = "function",
                Function = new FunctionDef
                {
                    Name = "agent_cancel",
                    Description = "Cancel a running job on an agent.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "agent_name":{"type":"string","description":"Name of the target agent"},
                        "job_id":{"type":"string","description":"The job ID to cancel"}
                      },
                      "required":["agent_name","job_id"]
                    }
                    """).RootElement
                }
            },

            // --- Sub-Agent Delegation Tool ---
            new Tool
            {
                Type = "function",
                Function = new FunctionDef
                {
                    Name = "delegate_to_agent",
                    Description = @"**IMPORTANT: Use this for complex tasks!** Delegate work to a specialist sub-agent for better results.

Available specialist roles:
- planner: Task analysis, implementation plans, architecture decisions
- coder: Code implementation, refactoring, multi-file changes  
- tester: Test writing, test execution, coverage analysis
- reviewer: Code review, security audit, best practices check
- debugger: Bug investigation, error analysis, fix proposals

USE THIS WHEN:
- Task involves 3+ files → delegate to coder
- Request mentions 'test' → delegate to tester
- Investigating bugs/errors → delegate to debugger
- Code review needed → delegate to reviewer
- Complex planning → delegate to planner

The sub-agent runs synchronously and returns a detailed result with actions taken.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "role":{"type":"string","enum":["planner","coder","tester","reviewer","debugger"],"description":"The specialist role to delegate to"},
                        "task":{"type":"string","description":"Clear description of the task for the sub-agent"},
                        "context_files":{"type":"array","items":{"type":"string"},"description":"Optional list of file paths the sub-agent should focus on"},
                        "success_criteria":{"type":"string","description":"Optional criteria for task completion"}
                      },
                      "required":["role","task"]
                    }
                    """).RootElement
                }
            },
            // LSP Code Intelligence
            new Tool
            {
                Type = "function",
                Function = new FunctionDef
                {
                    Name = "lsp",
                    Description = @"Interact with Language Server Protocol (LSP) for code intelligence. Provides type-aware navigation, references, diagnostics, and symbol search.

Operations:
- goToDefinition: Find where a symbol is defined (cross-file, cross-project)
- findReferences: Find all usages of a symbol (type-aware, not just text search)
- goToImplementation: Find implementations of an interface/abstract method
- hover: Get type info and documentation for a symbol
- documentSymbol: List all symbols (classes, methods, fields) in a file
- workspaceSymbol: Search symbols across the entire project (uses 'query' parameter)
- prepareCallHierarchy: Get call hierarchy item at a position
- incomingCalls: Find all functions/methods that call the function at a position
- outgoingCalls: Find all functions/methods called by the function at a position
- diagnostics: Get current compiler errors/warnings for a file

Line and character are 1-based (as shown in editors). For workspaceSymbol, use the 'query' parameter instead of line/character.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "operation":{"type":"string","enum":["goToDefinition","findReferences","goToImplementation","hover","documentSymbol","workspaceSymbol","prepareCallHierarchy","incomingCalls","outgoingCalls","diagnostics"],"description":"The LSP operation to perform"},
                        "filePath":{"type":"string","description":"Path to the file (absolute or relative to project root)"},
                        "line":{"type":"integer","minimum":1,"description":"Line number (1-based)"},
                        "character":{"type":"integer","minimum":1,"description":"Character offset (1-based)"},
                        "query":{"type":"string","description":"Search query for workspaceSymbol operation"}
                      },
                      "required":["operation","filePath"]
                    }
                    """).RootElement
                }
            }
        };

            return tools;
        }

        /// <summary>
        /// Get tools with categories, keywords, and deferred loading flags set.
        /// Use this instead of GetBuildTools() for optimized tool loading.
        /// </summary>
        public static List<Tool> GetCategorizedTools()
        {
            var tools = GetBuildTools();
            
            // Category mappings
            var categoryMap = new Dictionary<string, ToolCategory>(StringComparer.OrdinalIgnoreCase)
            {
                // Core - always loaded
                ["search_files"] = ToolCategory.Core,
                ["read_file"] = ToolCategory.Core,
                ["write_file"] = ToolCategory.Core,
                ["write_file_chunk"] = ToolCategory.Core,
                ["apply_patch"] = ToolCategory.Core,
                
                // Git
                ["git_status"] = ToolCategory.Git,
                ["git_diff"] = ToolCategory.Git,
                
                // Dotnet
                ["run_process"] = ToolCategory.Dotnet,
                ["dotnet_restore"] = ToolCategory.Dotnet,
                ["dotnet_build"] = ToolCategory.Dotnet,
                ["dotnet_test"] = ToolCategory.Dotnet,
                ["dotnet_run"] = ToolCategory.Dotnet,
                ["dotnet_new"] = ToolCategory.Dotnet,
                
                // NuGet
                ["nuget_search"] = ToolCategory.NuGet,
                ["nuget_add"] = ToolCategory.NuGet,
                
                // RAG
                ["rag_index"] = ToolCategory.Rag,
                ["rag_search"] = ToolCategory.Rag,
                ["rag_clear"] = ToolCategory.Rag,
                ["rag_stats"] = ToolCategory.Rag,
                
                // Browser
                ["browser_navigate"] = ToolCategory.Browser,
                ["browser_click"] = ToolCategory.Browser,
                ["browser_type"] = ToolCategory.Browser,
                ["browser_get_elements"] = ToolCategory.Browser,
                ["browser_screenshot"] = ToolCategory.Browser,
                ["browser_script"] = ToolCategory.Browser,
                ["browser_close"] = ToolCategory.Browser,
                
                // UI Automation
                ["analyze_image"] = ToolCategory.UIAutomation,
                ["ui_capture"] = ToolCategory.UIAutomation,
                ["ui_list_windows"] = ToolCategory.UIAutomation,
                ["ui_focus_window"] = ToolCategory.UIAutomation,
                ["ui_click"] = ToolCategory.UIAutomation,
                ["ui_type"] = ToolCategory.UIAutomation,
                ["ui_mouse_move"] = ToolCategory.UIAutomation,
                ["ui_get_element"] = ToolCategory.UIAutomation,
                ["ui_wait"] = ToolCategory.UIAutomation,
                
                // Process
                ["process_start"] = ToolCategory.Process,
                ["process_read"] = ToolCategory.Process,
                ["process_write"] = ToolCategory.Process,
                ["process_status"] = ToolCategory.Process,
                ["process_stop"] = ToolCategory.Process,
                
                // Code Index
                ["code_index"] = ToolCategory.CodeIndex,
                ["code_query"] = ToolCategory.CodeIndex,
                ["context_store"] = ToolCategory.CodeIndex,
                ["context_get"] = ToolCategory.CodeIndex,
                ["index_stats"] = ToolCategory.CodeIndex,
                ["index_clear"] = ToolCategory.CodeIndex,
                
                // Agents
                ["agent_list"] = ToolCategory.Agents,
                ["agent_submit"] = ToolCategory.Agents,
                ["agent_status"] = ToolCategory.Agents,
                ["agent_result"] = ToolCategory.Agents,
                ["agent_cancel"] = ToolCategory.Agents,
                ["delegate_to_agent"] = ToolCategory.Agents,
                
                // MCP
                ["execute_code"] = ToolCategory.Mcp,
                
                // LSP
                ["lsp"] = ToolCategory.CodeIndex
            };
            
            // Keywords for better search
            var keywordMap = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["search_files"] = new[] { "find", "glob", "pattern", "files", "grep" },
                ["read_file"] = new[] { "open", "get", "content", "view" },
                ["write_file"] = new[] { "save", "create", "update", "modify" },
                ["write_file_chunk"] = new[] { "save", "large", "chunk", "piece", "split" },
                ["apply_patch"] = new[] { "diff", "patch", "change", "modify", "edit" },
                ["git_status"] = new[] { "branch", "changes", "modified", "staged" },
                ["git_diff"] = new[] { "changes", "difference", "compare" },
                ["dotnet_build"] = new[] { "compile", "msbuild", "csharp" },
                ["dotnet_test"] = new[] { "xunit", "nunit", "mstest", "testing" },
                ["dotnet_run"] = new[] { "execute", "start", "launch" },
                ["rag_index"] = new[] { "embed", "vector", "semantic" },
                ["rag_search"] = new[] { "query", "semantic", "similar" },
                ["browser_navigate"] = new[] { "web", "url", "http", "page" },
                ["ui_capture"] = new[] { "screenshot", "screen", "capture", "image" },
                ["ui_click"] = new[] { "mouse", "click", "button", "interact" },
                ["ui_type"] = new[] { "keyboard", "input", "type", "text" },
                ["process_start"] = new[] { "run", "execute", "launch", "background" },
                ["code_index"] = new[] { "symbol", "class", "method", "parse" },
                ["code_query"] = new[] { "find", "symbol", "class", "method" },
                ["delegate_to_agent"] = new[] { "sub-agent", "specialist", "delegate", "helper" },
                ["lsp"] = new[] { "definition", "references", "hover", "symbol", "diagnostics", "type", "implementation", "calls" }
            };
            
            // Deferred categories (not Core)
            var deferredCategories = new HashSet<ToolCategory>
            {
                ToolCategory.Browser,
                ToolCategory.UIAutomation,
                ToolCategory.Process,
                ToolCategory.CodeIndex,
                ToolCategory.Agents,
                ToolCategory.Mcp,
                ToolCategory.Rag
            };
            
            // Apply categories and keywords
            foreach (var tool in tools)
            {
                var name = tool.Function.Name;
                
                if (categoryMap.TryGetValue(name, out var category))
                {
                    tool.Category = category;
                    tool.DeferLoading = deferredCategories.Contains(category);
                }
                
                if (keywordMap.TryGetValue(name, out var keywords))
                {
                    tool.SearchKeywords = keywords;
                }
            }
            
            return tools;
        }
        
        /// <summary>
        /// Get count of tools by category for diagnostics.
        /// </summary>
        public static Dictionary<ToolCategory, int> GetToolCountsByCategory()
        {
            var tools = GetCategorizedTools();
            return tools
                .GroupBy(t => t.Category)
                .ToDictionary(g => g.Key, g => g.Count());
        }
        
        /// <summary>
        /// Get tools based on configuration. If UseDeferredToolLoading is enabled,
        /// returns only core tools + tool_search/tool_load. Otherwise returns all tools.
        /// </summary>
        public static List<Tool> GetToolsForSession()
        {
            if (AgentConfig.Config.UseDeferredToolLoading)
            {
                // Initialize registry with categorized tools
                var registry = ToolRegistry.Instance;
                registry.ResetLoadedState();
                registry.RegisterTools(GetCategorizedTools());
                
                // Return only initial tools (core + tool_search + tool_load)
                var initialTools = registry.GetInitialTools();
                Console.WriteLine($"[BuildTools] Deferred loading enabled: {initialTools.Count} initial tools loaded");
                return initialTools;
            }
            else
            {
                // Return all tools (backward compatible)
                return GetBuildTools();
            }
        }
        
        /// <summary>
        /// Get a summary of tool counts for logging.
        /// </summary>
        public static string GetToolSummary()
        {
            var counts = GetToolCountsByCategory();
            var total = counts.Values.Sum();
            var categories = string.Join(", ", counts.Select(kvp => $"{kvp.Key}:{kvp.Value}"));
            return $"Total: {total} tools. By category: {categories}";
        }

    }
}
