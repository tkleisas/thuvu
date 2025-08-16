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
                    Description = "Read a file by absolute or relative path.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "path":{"type":"string","description":"Path to file"}
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
                    Description = "Write an entire file. Rejects if checksum mismatches when expected_sha256 is provided.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "path":{"type":"string"},
                        "content":{"type":"string"},
                        "expected_sha256":{"type":["string","null"],"description":"Checksum from read_file to prevent clobbering"},
                        "create_intermediate_dirs":{"type":"boolean","default":true}
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
                    Description = "Run 'dotnet run'.",
                    Parameters = JsonDocument.Parse("""
                    {
                      "type":"object",
                      "properties":{
                        "solution_or_project":{"type":"string"},
                        "filter":{"type":"string","description":"Run filter expression"},
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
                    Name = "dotnet_new",
                    Description = "Run 'dotnet new'.",
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
            }
        };

            return tools;
        }

    }
}
