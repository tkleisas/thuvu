using LmStudioInteractive;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace CodingAgent.Models
{
    public sealed class ChatMessage
    {
        public ChatMessage() { }

        public ChatMessage(string role, string? content, string? name = null, string? toolCallId = null)
        {
            Role = role; Content = content; Name = name; ToolCallId = toolCallId;
        }

        [JsonPropertyName("role")] public string Role { get; set; } = default!;
        [JsonPropertyName("content")] public string? Content { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; } // for tool result messages
        [JsonPropertyName("tool_call_id")] public string? ToolCallId { get; set; } // for tool result messages
        [JsonPropertyName("tool_calls")] public List<ToolCall>? ToolCalls { get; set; } // when assistant requests tools
    }
}
