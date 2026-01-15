using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace thuvu.Models
{
    /// <summary>
    /// Custom JSON converter for ChatMessage that handles multimodal content
    /// </summary>
    public class ChatMessageConverter : JsonConverter<ChatMessage>
    {
        public override ChatMessage? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            // For reading, use default deserialization
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            
            var msg = new ChatMessage
            {
                Role = root.GetProperty("role").GetString() ?? ""
            };
            
            if (root.TryGetProperty("content", out var contentEl))
            {
                if (contentEl.ValueKind == JsonValueKind.String)
                {
                    msg.Content = contentEl.GetString();
                }
                else if (contentEl.ValueKind == JsonValueKind.Array)
                {
                    msg.ContentParts = new List<ContentPart>();
                    foreach (var part in contentEl.EnumerateArray())
                    {
                        var cp = new ContentPart { Type = part.GetProperty("type").GetString() ?? "text" };
                        if (part.TryGetProperty("text", out var textEl))
                            cp.Text = textEl.GetString();
                        if (part.TryGetProperty("image_url", out var imgEl))
                        {
                            cp.ImageUrl = new ImageUrlContent { Url = imgEl.GetProperty("url").GetString() ?? "" };
                            if (imgEl.TryGetProperty("detail", out var detailEl))
                                cp.ImageUrl.Detail = detailEl.GetString();
                        }
                        msg.ContentParts.Add(cp);
                    }
                }
            }
            
            if (root.TryGetProperty("name", out var nameEl))
                msg.Name = nameEl.GetString();
            if (root.TryGetProperty("tool_call_id", out var toolCallIdEl))
                msg.ToolCallId = toolCallIdEl.GetString();
            if (root.TryGetProperty("tool_calls", out var toolCallsEl))
                msg.ToolCalls = JsonSerializer.Deserialize<List<ToolCall>>(toolCallsEl.GetRawText(), options);
            
            return msg;
        }

        public override void Write(Utf8JsonWriter writer, ChatMessage value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            
            writer.WriteString("role", value.Role);
            
            // Write content - either as string or array depending on message type
            if (value.IsMultimodal && value.ContentParts != null)
            {
                writer.WritePropertyName("content");
                writer.WriteStartArray();
                foreach (var part in value.ContentParts)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", part.Type);
                    if (part.Type == "text" && part.Text != null)
                    {
                        writer.WriteString("text", part.Text);
                    }
                    else if (part.Type == "image_url" && part.ImageUrl != null)
                    {
                        writer.WritePropertyName("image_url");
                        writer.WriteStartObject();
                        writer.WriteString("url", part.ImageUrl.Url);
                        if (part.ImageUrl.Detail != null)
                            writer.WriteString("detail", part.ImageUrl.Detail);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            else if (value.Content != null)
            {
                writer.WriteString("content", value.Content);
            }
            else
            {
                writer.WriteNull("content");
            }
            
            if (value.Name != null)
                writer.WriteString("name", value.Name);
            if (value.ToolCallId != null)
                writer.WriteString("tool_call_id", value.ToolCallId);
            if (value.ToolCalls != null)
            {
                writer.WritePropertyName("tool_calls");
                JsonSerializer.Serialize(writer, value.ToolCalls, options);
            }
            
            writer.WriteEndObject();
        }
    }
    
    [JsonConverter(typeof(ChatMessageConverter))]
    public sealed class ChatMessage
    {
        public ChatMessage() { }

        public ChatMessage(string role, string? content, string? name = null, string? toolCallId = null)
        {
            Role = role; Content = content; Name = name; ToolCallId = toolCallId;
        }
        
        /// <summary>
        /// Create a multimodal message with text and image
        /// </summary>
        public static ChatMessage CreateWithImage(string role, string text, string imageBase64, string mimeType)
        {
            return new ChatMessage
            {
                Role = role,
                ContentParts = new List<ContentPart>
                {
                    new ContentPart { Type = "text", Text = text },
                    new ContentPart 
                    { 
                        Type = "image_url", 
                        ImageUrl = new ImageUrlContent { Url = $"data:{mimeType};base64,{imageBase64}" }
                    }
                }
            };
        }

        [JsonPropertyName("role")] public string Role { get; set; } = default!;
        
        /// <summary>
        /// Text content (for simple text messages)
        /// </summary>
        [JsonPropertyName("content")] 
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Content { get; set; }
        
        /// <summary>
        /// Multimodal content parts (for messages with images)
        /// When serializing, if ContentParts is set, it should be used as "content" instead of Content
        /// </summary>
        [JsonIgnore]
        public List<ContentPart>? ContentParts { get; set; }
        
        /// <summary>
        /// Check if this is a multimodal message
        /// </summary>
        [JsonIgnore]
        public bool IsMultimodal => ContentParts != null && ContentParts.Count > 0;
        
        /// <summary>
        /// Get text content regardless of message type
        /// </summary>
        [JsonIgnore]
        public string? TextContent => IsMultimodal 
            ? ContentParts?.FirstOrDefault(p => p.Type == "text")?.Text 
            : Content;
        
        [JsonPropertyName("name")] public string? Name { get; set; } // for tool result messages
        [JsonPropertyName("tool_call_id")] public string? ToolCallId { get; set; } // for tool result messages
        [JsonPropertyName("tool_calls")] public List<ToolCall>? ToolCalls { get; set; } // when assistant requests tools
    }
    
    /// <summary>
    /// Content part for multimodal messages (OpenAI format)
    /// </summary>
    public class ContentPart
    {
        [JsonPropertyName("type")] 
        public string Type { get; set; } = "text";
        
        [JsonPropertyName("text")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Text { get; set; }
        
        [JsonPropertyName("image_url")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ImageUrlContent? ImageUrl { get; set; }
    }
    
    /// <summary>
    /// Image URL content for vision messages
    /// </summary>
    public class ImageUrlContent
    {
        [JsonPropertyName("url")]
        public string Url { get; set; } = "";
        
        [JsonPropertyName("detail")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Detail { get; set; } // "auto", "low", "high"
    }
}
