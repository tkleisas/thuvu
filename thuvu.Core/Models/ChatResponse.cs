using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace thuvu.Models
{
    public sealed class ChatResponse
    {
        [JsonPropertyName("choices")] public List<Choice> Choices { get; set; } = default!;
        [JsonPropertyName("usage")] public Usage? Usage { get; set; }
    }
}
