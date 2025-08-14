using LmStudioInteractive;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace CodingAgent.Models
{
    public sealed class Choice
    {
        [JsonPropertyName("message")] public ChatMessage Message { get; set; } = default!;
    }
}
