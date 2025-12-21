using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace thuvu.Models
{
    /// <summary>
    /// Custom JSON converter for List of ModelPurpose that handles string enum values
    /// </summary>
    public class ModelPurposeListConverter : JsonConverter<List<ModelPurpose>>
    {
        public override List<ModelPurpose> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var list = new List<ModelPurpose>();
            if (reader.TokenType != JsonTokenType.StartArray)
                return list;
                
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType == JsonTokenType.String)
                {
                    var value = reader.GetString();
                    if (Enum.TryParse<ModelPurpose>(value, true, out var purpose))
                    {
                        list.Add(purpose);
                    }
                }
                else if (reader.TokenType == JsonTokenType.Number)
                {
                    list.Add((ModelPurpose)reader.GetInt32());
                }
            }
            return list;
        }

        public override void Write(Utf8JsonWriter writer, List<ModelPurpose> value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (var purpose in value)
            {
                writer.WriteStringValue(purpose.ToString());
            }
            writer.WriteEndArray();
        }
    }

    /// <summary>
    /// Defines the purpose/task type for model selection
    /// </summary>
    public enum ModelPurpose
    {
        /// <summary>Default model for general chat and coding</summary>
        Default,
        /// <summary>Complex reasoning, planning, architecture decisions</summary>
        Thinking,
        /// <summary>Simple code generation, refactoring, formatting</summary>
        Coding,
        /// <summary>Code review and analysis</summary>
        Review,
        /// <summary>Summarization and documentation</summary>
        Summary,
        /// <summary>Embedding generation for RAG</summary>
        Embedding
    }

    /// <summary>
    /// Configuration for a single LLM model endpoint
    /// </summary>
    public class ModelEndpoint
    {
        /// <summary>Model identifier (e.g., "qwen/qwen3-coder-30b")</summary>
        public string ModelId { get; set; } = "";
        
        /// <summary>Display name for UI</summary>
        public string DisplayName { get; set; } = "";
        
        /// <summary>Base URL for the API (e.g., "http://127.0.0.1:1234")</summary>
        public string HostUrl { get; set; } = "http://127.0.0.1:1234";
        
        /// <summary>Whether this is a local model (affects timeout and retry behavior)</summary>
        public bool IsLocal { get; set; } = true;
        
        /// <summary>Whether to stream responses</summary>
        public bool Stream { get; set; } = true;
        
        /// <summary>HTTP request timeout in minutes</summary>
        public int TimeoutMinutes { get; set; } = 60;
        
        /// <summary>Authentication scheme (e.g., "Bearer")</summary>
        public string AuthScheme { get; set; } = "";
        
        /// <summary>Authentication token</summary>
        public string AuthToken { get; set; } = "";
        
        /// <summary>Custom header name for auth (default: Authorization)</summary>
        public string AuthHeaderName { get; set; } = "Authorization";
        
        /// <summary>Maximum context length (0 = auto-detect)</summary>
        public int MaxContextLength { get; set; } = 0;
        
        /// <summary>Maximum output tokens (0 = use default)</summary>
        public int MaxOutputTokens { get; set; } = 0;
        
        /// <summary>Temperature for generation (0.0 - 2.0)</summary>
        public double Temperature { get; set; } = 0.2;
        
        /// <summary>Whether this model supports tool/function calling</summary>
        public bool SupportsTools { get; set; } = true;
        
        /// <summary>Whether this is a "thinking" model that shows reasoning</summary>
        public bool IsThinkingModel { get; set; } = false;
        
        /// <summary>Purposes this model can be used for</summary>
        [JsonConverter(typeof(ModelPurposeListConverter))]
        public List<ModelPurpose> Purposes { get; set; } = new() { ModelPurpose.Default };
        
        /// <summary>Priority for selection (higher = preferred)</summary>
        public int Priority { get; set; } = 0;
        
        /// <summary>Whether this model is enabled</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Creates an HttpClient configured for this model endpoint
        /// </summary>
        public HttpClient CreateHttpClient()
        {
            var client = new HttpClient
            {
                BaseAddress = new Uri(HostUrl),
                Timeout = TimeSpan.FromMinutes(TimeoutMinutes)
            };
            
            // Apply authentication if configured
            if (!string.IsNullOrWhiteSpace(AuthToken))
            {
                if (!string.IsNullOrWhiteSpace(AuthScheme))
                {
                    client.DefaultRequestHeaders.Authorization = 
                        new System.Net.Http.Headers.AuthenticationHeaderValue(AuthScheme, AuthToken);
                }
                else
                {
                    client.DefaultRequestHeaders.TryAddWithoutValidation(
                        string.IsNullOrWhiteSpace(AuthHeaderName) ? "Authorization" : AuthHeaderName, 
                        AuthToken);
                }
            }
            
            return client;
        }
        
        public override string ToString() => 
            $"{DisplayName ?? ModelId} @ {HostUrl} [{string.Join(", ", Purposes)}]";
    }

    /// <summary>
    /// Multi-model configuration manager
    /// </summary>
    public class ModelRegistry
    {
        private static ModelRegistry? _instance;
        private static readonly object _lock = new();
        
        /// <summary>List of configured model endpoints</summary>
        public List<ModelEndpoint> Models { get; set; } = new();
        
        /// <summary>Default model ID to use when no specific purpose is specified</summary>
        public string DefaultModelId { get; set; } = "";
        
        /// <summary>Model ID to use for thinking/planning tasks</summary>
        public string ThinkingModelId { get; set; } = "";
        
        /// <summary>Model ID to use for simple coding tasks</summary>
        public string CodingModelId { get; set; } = "";
        
        /// <summary>Model ID to use for embeddings</summary>
        public string EmbeddingModelId { get; set; } = "";
        
        /// <summary>Whether to automatically select models based on task</summary>
        public bool AutoSelectModel { get; set; } = true;
        
        /// <summary>Singleton instance</summary>
        public static ModelRegistry Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        _instance ??= new ModelRegistry();
                    }
                }
                return _instance;
            }
        }
        
        /// <summary>
        /// Get a model endpoint by ID
        /// </summary>
        public ModelEndpoint? GetModel(string modelId)
        {
            return Models.Find(m => m.ModelId.Equals(modelId, StringComparison.OrdinalIgnoreCase) ||
                                    m.DisplayName.Equals(modelId, StringComparison.OrdinalIgnoreCase));
        }
        
        /// <summary>
        /// Get the best model for a specific purpose
        /// </summary>
        public ModelEndpoint? GetModelForPurpose(ModelPurpose purpose)
        {
            // First check explicit assignments
            var explicitId = purpose switch
            {
                ModelPurpose.Thinking => ThinkingModelId,
                ModelPurpose.Coding => CodingModelId,
                ModelPurpose.Embedding => EmbeddingModelId,
                _ => DefaultModelId
            };
            
            if (!string.IsNullOrWhiteSpace(explicitId))
            {
                var explicit_ = GetModel(explicitId);
                if (explicit_?.Enabled == true)
                    return explicit_;
            }
            
            // Fall back to finding a model that supports this purpose
            var candidates = Models
                .Where(m => m.Enabled && m.Purposes.Contains(purpose))
                .OrderByDescending(m => m.Priority)
                .ToList();
            
            if (candidates.Count > 0)
                return candidates[0];
            
            // Final fallback: default model or first enabled model
            var defaultModel = GetModel(DefaultModelId);
            if (defaultModel?.Enabled == true)
                return defaultModel;
            
            return Models.FirstOrDefault(m => m.Enabled);
        }
        
        /// <summary>
        /// Get the default model
        /// </summary>
        public ModelEndpoint? GetDefaultModel() => GetModelForPurpose(ModelPurpose.Default);
        
        /// <summary>
        /// Get thinking model for complex reasoning
        /// </summary>
        public ModelEndpoint? GetThinkingModel() => GetModelForPurpose(ModelPurpose.Thinking);
        
        /// <summary>
        /// Get coding model for simple code tasks
        /// </summary>
        public ModelEndpoint? GetCodingModel() => GetModelForPurpose(ModelPurpose.Coding);
        
        /// <summary>
        /// Add a model endpoint
        /// </summary>
        public void AddModel(ModelEndpoint model)
        {
            // Remove existing with same ID
            Models.RemoveAll(m => m.ModelId.Equals(model.ModelId, StringComparison.OrdinalIgnoreCase));
            Models.Add(model);
        }
        
        /// <summary>
        /// Initialize from AgentConfig for backward compatibility
        /// </summary>
        public static void InitializeFromAgentConfig()
        {
            lock (_lock)
            {
                _instance = new ModelRegistry();
                
                // Create a default model from AgentConfig
                var defaultModel = new ModelEndpoint
                {
                    ModelId = AgentConfig.Config.Model,
                    DisplayName = AgentConfig.Config.Model,
                    HostUrl = AgentConfig.Config.HostUrl,
                    IsLocal = true,
                    Stream = AgentConfig.Config.Stream,
                    TimeoutMinutes = AgentConfig.Config.HttpRequestTimeout,
                    AuthScheme = AgentConfig.Config.AuthScheme,
                    AuthToken = AgentConfig.Config.AuthToken,
                    AuthHeaderName = AgentConfig.Config.AuthHeaderName,
                    SupportsTools = true,
                    Purposes = new List<ModelPurpose> 
                    { 
                        ModelPurpose.Default, 
                        ModelPurpose.Coding, 
                        ModelPurpose.Thinking,
                        ModelPurpose.Review,
                        ModelPurpose.Summary
                    },
                    Priority = 10,
                    Enabled = true
                };
                
                _instance.Models.Add(defaultModel);
                _instance.DefaultModelId = defaultModel.ModelId;
            }
        }
        
        /// <summary>
        /// Load models configuration from JSON section
        /// </summary>
        public static void LoadFromJson(System.Text.Json.JsonElement element)
        {
            lock (_lock)
            {
                try
                {
                    var options = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    _instance = System.Text.Json.JsonSerializer.Deserialize<ModelRegistry>(element.GetRawText(), options) 
                                ?? new ModelRegistry();
                    AgentLogger.LogInfo("Loaded {Count} model configurations", _instance.Models.Count);
                    
                    // Sync DefaultModelId with AgentConfig.Config.Model
                    if (!string.IsNullOrEmpty(_instance.DefaultModelId))
                    {
                        var defaultModel = _instance.Models.FirstOrDefault(m => m.ModelId == _instance.DefaultModelId);
                        if (defaultModel != null)
                        {
                            AgentConfig.Config.Model = defaultModel.ModelId;
                            AgentConfig.Config.HostUrl = defaultModel.HostUrl;
                            AgentConfig.Config.Stream = defaultModel.Stream;
                            
                            // Set auth token if configured on the model
                            if (!string.IsNullOrEmpty(defaultModel.AuthToken))
                            {
                                AgentConfig.Config.AuthToken = defaultModel.AuthToken;
                            }
                            
                            AgentLogger.LogInfo("Set default model to: {Model} at {Host}", defaultModel.ModelId, defaultModel.HostUrl);
                        }
                    }
                }
                catch (Exception ex)
                {
                    AgentLogger.LogError("Failed to load model registry: {Error}", ex.Message);
                    InitializeFromAgentConfig();
                }
            }
        }
        
        /// <summary>
        /// List all configured models
        /// </summary>
        public void PrintModels()
        {
            ConsoleHelpers.PrintDivider("Configured Models", ConsoleColor.Cyan);
            foreach (var model in Models)
            {
                var status = model.Enabled ? "✓" : "✗";
                var local = model.IsLocal ? "local" : "remote";
                var thinking = model.IsThinkingModel ? " [thinking]" : "";
                ConsoleHelpers.WithColor(model.Enabled ? ConsoleColor.Green : ConsoleColor.DarkGray, 
                    () => Console.Write($" {status} "));
                Console.WriteLine($"{model.DisplayName ?? model.ModelId} ({local}{thinking})");
                ConsoleHelpers.WithColor(ConsoleColor.DarkGray, 
                    () => Console.WriteLine($"    {model.HostUrl} | Purposes: {string.Join(", ", model.Purposes)}"));
            }
            Console.WriteLine();
            ConsoleHelpers.PrintKeyValue("Default", DefaultModelId);
            ConsoleHelpers.PrintKeyValue("Thinking", string.IsNullOrEmpty(ThinkingModelId) ? "(auto)" : ThinkingModelId);
            ConsoleHelpers.PrintKeyValue("Coding", string.IsNullOrEmpty(CodingModelId) ? "(auto)" : CodingModelId);
        }
    }
}
