using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace CodingAgent.Models
{
    public sealed class FunctionDef
    {
        [JsonPropertyName("name")] public string Name { get; set; } = default!;
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("parameters")] public JsonElement Parameters { get; set; } // raw JSON schema
    }
}
