using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace thuvu.Models
{
    public sealed class FunctionCall
    {
        [JsonPropertyName("name")] public string Name { get; set; } = default!;
        // LM Studio returns arguments as a JSON string
        [JsonPropertyName("arguments")] public string? Arguments { get; set; }
    }
}
