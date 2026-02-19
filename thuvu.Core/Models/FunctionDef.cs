using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace thuvu.Models
{
    public sealed class FunctionDef
    {
        [JsonPropertyName("name")] public string Name { get; set; } = default!;
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("parameters")] public JsonElement Parameters { get; set; } // raw JSON schema
        
        /// <summary>
        /// Example inputs demonstrating correct usage patterns.
        /// Not serialized to API but used for generating tool documentation.
        /// </summary>
        [JsonIgnore]
        public List<object>? InputExamples { get; set; }
    }
}
