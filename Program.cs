using CodingAgent.BuildTools;
using CodingAgent.Models;
using CodingAgent.Tools;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LmStudioInteractive
{
    internal class Program
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        private static readonly Uri BaseUri = new("http://127.0.0.1:1234"); // LM Studio default
        private const string DefaultModel = "";

        public static async Task Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            var model = DefaultModel;
            using var http = new HttpClient { BaseAddress = BaseUri, Timeout = TimeSpan.FromMinutes(10) };

            // Conversation state
            var messages = new List<ChatMessage>
            {
                new(
                    "system",
                    "You are a helpful coding agent.Prefer tools over guessing;"+
                    " never invent file paths. When modifying code: "+
                    "(1) read_file, "+
                    "(2) propose a minimal unified diff via apply_patch, "+
                    "(3) run dotnet_build and dotnet_test, "+
                    "(4) if green, you may git_commit with a concise message. " +
                    "If write_file returns checksum_mismatch, re-read the file and rebase your patch."+
                    "Use search_files before claiming a symbol/file doesn’t exist.")
            };

            // Tools you expose to the model
            var tools = BuildTools.GetBuildTools();

            Console.WriteLine("LM Studio Interactive (type /exit to quit, /clear to reset, /system <text> to set system prompt)");
            Console.WriteLine($"Model: {model}");
            Console.WriteLine();

            while (true)
            {
                Console.Write("> ");
                var user = Console.ReadLine();
                if (user == null) continue;

                // Commands
                if (user.Equals("/exit", StringComparison.OrdinalIgnoreCase)) break;

                if (user.StartsWith("/clear", StringComparison.OrdinalIgnoreCase))
                {
                    messages = new List<ChatMessage> { new("system", messages[0].Content) };
                    Console.WriteLine("Conversation cleared.");
                    continue;
                }

                if (user.StartsWith("/system ", StringComparison.OrdinalIgnoreCase))
                {
                    var sys = user.Substring(8).Trim();
                    if (string.IsNullOrWhiteSpace(sys))
                    {
                        Console.WriteLine("Usage: /system <text>");
                        continue;
                    }
                    messages[0] = new ChatMessage("system", sys);
                    Console.WriteLine("System prompt updated.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(user)) continue;

                messages.Add(new ChatMessage("user", user));

                // Send to LM Studio and run tool loop until final answer
                var final = await CompleteWithToolsAsync(http, model, messages, tools, CancellationToken.None);

                if (!string.IsNullOrEmpty(final))
                {
                    Console.WriteLine();
                    Console.WriteLine(final);
                    Console.WriteLine();
                    messages.Add(new ChatMessage("assistant", final));
                }
                else
                {
                    Console.WriteLine("(no content)");
                }
            }
        }

        

        /// <summary>
        /// Sends the current conversation to LM Studio. If the assistant requests tools,
        /// executes them, appends tool results, and repeats until a final answer is produced.
        /// Returns the assistant's final content.
        /// </summary>
        private static async Task<string?> CompleteWithToolsAsync(
            HttpClient http,
            string model,
            List<ChatMessage> messages,
            List<Tool> tools,
            CancellationToken ct)
        {
            int counter = 0;
            int toolcounter = 0;
            while (true)
            {
                var req = new ChatRequest
                {
                    Model = model,
                    Messages = messages,
                    Tools = tools,
                    ToolChoice = "auto",
                    Temperature = 0.2
                };
                
                using var resp = await http.PostAsJsonAsync("/v1/chat/completions", req, JsonOpts, ct);
                resp.EnsureSuccessStatusCode();
                counter++;
                var body = await resp.Content.ReadFromJsonAsync<ChatResponse>(JsonOpts, ct)
                           ?? throw new InvalidOperationException("Empty response from LM Studio.");
                var msg = body.Choices[0].Message;

                // If the assistant requested tool(s)…
                if (msg.ToolCalls is { Count: > 0 })
                {
                    // Append the assistant message that *requested* tools,
                    // so the next turn has the link to tool_call_ids
                    messages.Add(new ChatMessage
                    {
                        Role = "assistant",
                        Content = null,
                        ToolCalls = msg.ToolCalls
                    });

                    foreach (var call in msg.ToolCalls)
                    {
                        var name = call.Function.Name;
                        var argsJson = call.Function.Arguments ?? "{}";
                        var result = await ExecuteToolAsync(name, argsJson, CancellationToken.None);
                        toolcounter++;
                        messages.Add(new ChatMessage(
                            role: "tool",
                            content: result,
                            name: name,
                            toolCallId: call.Id
                        ));
                        Console.Write($"\rcalls:({counter}-{toolcounter}) tool:{name}               \r");
                    }

                    // loop continues: the appended tool results are now part of messages
                    continue;
                }

                // No tool calls → final answer in content
                return msg.Content;
            }
        }

        /// <summary>
        /// Executes a tool by name, returning a JSON string result.
        /// </summary>
        private static async Task<string> ExecuteToolAsync(string name, string argsJson,CancellationToken ct)
        {
            try
            {
                switch (name)
                {
                    // Navigation / IO
                    case "search_files":
                        return JsonSerializer.Serialize(new { matches = SearchFilesToolImpl.SearchFilesTool(argsJson) });

                    case "read_file":
                        return ReadFileToolImpl.ReadFileTool(argsJson);

                    case "write_file":
                        return WriteFileToolImpl.WriteFileTool(argsJson);

                    case "apply_patch":
                        return ApplyPatchToolImpl.ApplyPatchTool(argsJson);

                    // Process runner
                    case "run_process":
                        return await RunProcessToolImpl. RunProcessToolAsync(argsJson);

                    // dotnet
                    case "dotnet_restore":
                        return await DotnetToolImpl.DotnetRestoreTool(argsJson);

                    case "dotnet_build":
                        return await DotnetToolImpl.DotnetBuildTool(argsJson);

                    case "dotnet_test":
                        return await DotnetToolImpl.DotnetTestTool(argsJson);
                    case "dotnet_run":
                        return await DotnetToolImpl.DotnetRunTool(argsJson);
                    // git
                    case "git_status":
                        return await RunProcessToolImpl.RunProcessToolAsync(JsonSerializer.Serialize(Helpers.BuildGitStatusArgs(argsJson)));

                    case "git_diff":
                        return await RunProcessToolImpl.RunProcessToolAsync(JsonSerializer.Serialize(Helpers.BuildGitDiffArgs(argsJson)));

                    // NuGet
                    case "nuget_search":
                        return await RunProcessToolImpl.RunProcessToolAsync(JsonSerializer.Serialize(new
                        {
                            cmd = "dotnet",
                            args = new[] { "nuget", "search", Helpers.ExtractQuery(argsJson) }
                        }));

                    case "nuget_add":
                        return await RunProcessToolImpl.RunProcessToolAsync(JsonSerializer.Serialize(Helpers.BuildNugetAddArgs(argsJson)));

                    default:
                        return JsonSerializer.Serialize(new { error = $"Unknown tool: {name}" });
                }
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { error = ex.Message });
            }
        }
    }

    

}
