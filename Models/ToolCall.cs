using LmStudioInteractive;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace CodingAgent.Models
{
    public sealed class ToolCall
    {
        [JsonPropertyName("id")] public string Id { get; set; } = default!;
        [JsonPropertyName("type")] public string Type { get; set; } = "function";
        [JsonPropertyName("function")] public FunctionCall Function { get; set; } = default!;
    }
}
