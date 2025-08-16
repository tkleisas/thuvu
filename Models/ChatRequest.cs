using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace thuvu.Models
{
    public sealed class ChatRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = default!;
        [JsonPropertyName("messages")] public List<ChatMessage> Messages { get; set; } = default!;
        [JsonPropertyName("tools")] public List<Tool>? Tools { get; set; }
        [JsonPropertyName("tool_choice")] public string? ToolChoice { get; set; }
        [JsonPropertyName("temperature")] public double? Temperature { get; set; }
    }
}
