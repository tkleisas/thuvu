using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace thuvu.Models
{
    public sealed class Usage
    {
        [JsonPropertyName("prompt_tokens")] public int PromptTokens { get; set; }
        [JsonPropertyName("completion_tokens")] public int CompletionTokens { get; set; }
        [JsonPropertyName("total_tokens")] public int TotalTokens { get; set; }
        
        // DeepSeek-specific fields for cache tracking
        [JsonPropertyName("prompt_cache_hit_tokens")] public int PromptCacheHitTokens { get; set; }
        [JsonPropertyName("prompt_cache_miss_tokens")] public int PromptCacheMissTokens { get; set; }
        
        // Some APIs report context length in usage
        [JsonPropertyName("context_length")] public int? ContextLength { get; set; }
        [JsonPropertyName("max_context_length")] public int? MaxContextLength { get; set; }
    }
}
