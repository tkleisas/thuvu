using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using thuvu.Models;

namespace thuvu.Tools
{
    /// <summary>
    /// Tool for analyzing images using a vision-capable LLM
    /// </summary>
    public static class VisionToolImpl
    {
        /// <summary>
        /// Analyze an image using a vision-capable LLM
        /// </summary>
        /// <param name="argsJson">JSON with "image_path" (or "image_base64") and optional "prompt"</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>JSON result with analysis description</returns>
        public static async Task<string> AnalyzeImageAsync(string argsJson, CancellationToken ct = default)
        {
            try
            {
                using var doc = JsonDocument.Parse(argsJson);
                var root = doc.RootElement;
                
                // Get image data
                string? imagePath = null;
                string? imageBase64 = null;
                string mimeType = "image/png";
                
                if (root.TryGetProperty("image_path", out var pathEl))
                {
                    imagePath = pathEl.GetString();
                }
                
                if (root.TryGetProperty("image_base64", out var base64El))
                {
                    imageBase64 = base64El.GetString();
                }
                
                // Get optional prompt
                var prompt = root.TryGetProperty("prompt", out var promptEl) 
                    ? promptEl.GetString() ?? "Describe this image in detail."
                    : "Describe this image in detail.";
                
                // Load image if path provided
                if (!string.IsNullOrEmpty(imagePath))
                {
                    var workDir = AgentConfig.GetWorkDirectory();
                    var fullPath = Path.IsPathRooted(imagePath) 
                        ? imagePath 
                        : Path.GetFullPath(Path.Combine(workDir, imagePath));
                    
                    if (!File.Exists(fullPath))
                    {
                        return JsonSerializer.Serialize(new { 
                            success = false, 
                            error = $"Image file not found: {fullPath}" 
                        });
                    }
                    
                    var bytes = await File.ReadAllBytesAsync(fullPath, ct);
                    imageBase64 = Convert.ToBase64String(bytes);
                    
                    // Detect MIME type from extension
                    var ext = Path.GetExtension(fullPath).ToLowerInvariant();
                    mimeType = ext switch
                    {
                        ".jpg" or ".jpeg" => "image/jpeg",
                        ".png" => "image/png",
                        ".gif" => "image/gif",
                        ".webp" => "image/webp",
                        ".bmp" => "image/bmp",
                        _ => "image/png"
                    };
                }
                
                if (string.IsNullOrEmpty(imageBase64))
                {
                    return JsonSerializer.Serialize(new { 
                        success = false, 
                        error = "No image provided. Use 'image_path' or 'image_base64'" 
                    });
                }
                
                // Get vision model
                var visionModel = ModelRegistry.Instance.GetVisionModel();
                if (visionModel == null || !visionModel.SupportsVision)
                {
                    return JsonSerializer.Serialize(new { 
                        success = false, 
                        error = "No vision-capable model configured. Add a model with SupportsVision=true and Purposes=['Vision']" 
                    });
                }
                
                // Call the vision model
                var result = await CallVisionModelAsync(visionModel, imageBase64, mimeType, prompt, ct);
                
                return JsonSerializer.Serialize(new { 
                    success = true, 
                    description = result,
                    model = visionModel.ModelId
                });
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { 
                    success = false, 
                    error = ex.Message 
                });
            }
        }
        
        /// <summary>
        /// Analyze an image directly with base64 data
        /// </summary>
        public static async Task<VisionResult> AnalyzeImageAsync(
            string imageBase64, 
            string mimeType, 
            string prompt,
            CancellationToken ct = default)
        {
            try
            {
                // Get vision model
                var visionModel = ModelRegistry.Instance.GetVisionModel();
                if (visionModel == null || !visionModel.SupportsVision)
                {
                    return new VisionResult 
                    { 
                        Success = false, 
                        Error = "No vision-capable model configured. Add a model with SupportsVision=true" 
                    };
                }
                
                // Call the vision model
                var description = await CallVisionModelAsync(visionModel, imageBase64, mimeType, prompt, ct);
                
                return new VisionResult 
                { 
                    Success = true, 
                    Description = description,
                    Model = visionModel.ModelId
                };
            }
            catch (Exception ex)
            {
                return new VisionResult { Success = false, Error = ex.Message };
            }
        }
        
        /// <summary>
        /// Call the vision model with an image
        /// </summary>
        private static async Task<string> CallVisionModelAsync(
            ModelEndpoint model, 
            string imageBase64, 
            string mimeType,
            string prompt,
            CancellationToken ct)
        {
            using var client = model.CreateHttpClient();
            
            // Build the request in OpenAI vision format
            var request = new
            {
                model = model.ModelId,
                messages = new[]
                {
                    new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new { type = "text", text = prompt },
                            new { 
                                type = "image_url", 
                                image_url = new { 
                                    url = $"data:{mimeType};base64,{imageBase64}" 
                                }
                            }
                        }
                    }
                },
                max_tokens = model.MaxOutputTokens > 0 ? model.MaxOutputTokens : 1024,
                temperature = model.Temperature
            };
            
            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            var response = await client.PostAsync("/v1/chat/completions", content, ct);
            var responseJson = await response.Content.ReadAsStringAsync(ct);
            
            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"Vision API error: {response.StatusCode} - {responseJson}");
            }
            
            // Parse response
            using var responseDoc = JsonDocument.Parse(responseJson);
            var choices = responseDoc.RootElement.GetProperty("choices");
            if (choices.GetArrayLength() > 0)
            {
                var message = choices[0].GetProperty("message");
                var text = message.GetProperty("content").GetString();
                return text ?? "No response from vision model";
            }
            
            return "No response from vision model";
        }
    }
    
    /// <summary>
    /// Result from vision analysis
    /// </summary>
    public class VisionResult
    {
        public bool Success { get; set; }
        public string? Description { get; set; }
        public string? Error { get; set; }
        public string? Model { get; set; }
    }
}
