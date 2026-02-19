using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using thuvu.Models;

namespace thuvu.Desktop.ViewModels;

/// <summary>
/// ViewModel for a single provider/model endpoint in the settings UI
/// </summary>
public partial class ProviderViewModel : ObservableObject
{
    [ObservableProperty] private string _modelId = "";
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string _hostUrl = "http://127.0.0.1:1234";
    [ObservableProperty] private string _authScheme = "Bearer";
    [ObservableProperty] private string _authToken = "";
    [ObservableProperty] private string _chatCompletionsPath = "";
    [ObservableProperty] private bool _isLocal = true;
    [ObservableProperty] private bool _stream = true;
    [ObservableProperty] private bool _supportsTools = true;
    [ObservableProperty] private bool _supportsVision;
    [ObservableProperty] private bool _isThinkingModel;
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private int _maxContextLength;
    [ObservableProperty] private int _maxOutputTokens;
    [ObservableProperty] private double _temperature = 0.2;
    [ObservableProperty] private int _timeoutMinutes = 60;

    public static ProviderViewModel FromEndpoint(ModelEndpoint ep) => new()
    {
        ModelId = ep.ModelId,
        DisplayName = ep.DisplayName,
        HostUrl = ep.HostUrl,
        AuthScheme = ep.AuthScheme,
        AuthToken = ep.AuthToken,
        ChatCompletionsPath = ep.ChatCompletionsPath,
        IsLocal = ep.IsLocal,
        Stream = ep.Stream,
        SupportsTools = ep.SupportsTools,
        SupportsVision = ep.SupportsVision,
        IsThinkingModel = ep.IsThinkingModel,
        Enabled = ep.Enabled,
        MaxContextLength = ep.MaxContextLength,
        MaxOutputTokens = ep.MaxOutputTokens,
        Temperature = ep.Temperature,
        TimeoutMinutes = ep.TimeoutMinutes,
    };

    public ModelEndpoint ToEndpoint() => new()
    {
        ModelId = ModelId,
        DisplayName = DisplayName,
        HostUrl = HostUrl,
        AuthScheme = AuthScheme,
        AuthToken = AuthToken,
        ChatCompletionsPath = ChatCompletionsPath,
        IsLocal = IsLocal,
        Stream = Stream,
        SupportsTools = SupportsTools,
        SupportsVision = SupportsVision,
        IsThinkingModel = IsThinkingModel,
        Enabled = Enabled,
        MaxContextLength = MaxContextLength,
        MaxOutputTokens = MaxOutputTokens,
        Temperature = Temperature,
        TimeoutMinutes = TimeoutMinutes,
    };

    public override string ToString() =>
        string.IsNullOrEmpty(DisplayName) ? ModelId : $"{DisplayName} ({ModelId})";
}

/// <summary>
/// ViewModel for the Settings window
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    public ObservableCollection<ProviderViewModel> Providers { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveProviderCommand))]
    [NotifyCanExecuteChangedFor(nameof(DuplicateProviderCommand))]
    private ProviderViewModel? _selectedProvider;

    // Agent settings
    [ObservableProperty] private int _maxIterations = 50;
    [ObservableProperty] private int _timeoutSeconds = 1800;
    [ObservableProperty] private bool _streaming = true;
    [ObservableProperty] private bool _autoApproveTools;
    [ObservableProperty] private string _workDirectory = "";
    [ObservableProperty] private string _defaultModelId = "";

    // Feature toggles
    [ObservableProperty] private bool _mcpEnabled;
    [ObservableProperty] private bool _ragEnabled;

    public SettingsViewModel()
    {
        LoadFromConfig();
    }

    private void LoadFromConfig()
    {
        var config = AgentConfig.Config;
        MaxIterations = config.MaxIterations;
        TimeoutSeconds = config.TimeoutMs / 1000;
        Streaming = config.Stream;
        AutoApproveTools = config.AutoApproveTuiTools;
        WorkDirectory = config.WorkDirectory;
        DefaultModelId = ModelRegistry.Instance.DefaultModelId;
        McpEnabled = McpConfig.Instance.Enabled;
        RagEnabled = RagConfig.Instance.Enabled;

        Providers.Clear();
        foreach (var ep in ModelRegistry.Instance.Models)
            Providers.Add(ProviderViewModel.FromEndpoint(ep));

        // If no providers loaded, add one from AgentConfig for backwards compat
        if (Providers.Count == 0)
        {
            Providers.Add(new ProviderViewModel
            {
                ModelId = config.Model,
                DisplayName = config.Model,
                HostUrl = config.HostUrl,
                AuthScheme = config.AuthScheme,
                AuthToken = config.AuthToken,
                Stream = config.Stream,
                Enabled = true,
            });
        }

        if (Providers.Count > 0)
            SelectedProvider = Providers[0];
    }

    [RelayCommand]
    private void AddProvider()
    {
        var provider = new ProviderViewModel
        {
            ModelId = "new-model",
            DisplayName = "New Provider",
            HostUrl = "http://127.0.0.1:1234",
            Enabled = true,
            Stream = true,
            SupportsTools = true,
        };
        Providers.Add(provider);
        SelectedProvider = provider;
    }

    [RelayCommand(CanExecute = nameof(HasSelectedProvider))]
    private void RemoveProvider()
    {
        if (SelectedProvider == null) return;
        var idx = Providers.IndexOf(SelectedProvider);
        Providers.Remove(SelectedProvider);
        if (Providers.Count > 0)
            SelectedProvider = Providers[Math.Min(idx, Providers.Count - 1)];
        else
            SelectedProvider = null;
    }

    [RelayCommand(CanExecute = nameof(HasSelectedProvider))]
    private void DuplicateProvider()
    {
        if (SelectedProvider == null) return;
        var ep = SelectedProvider.ToEndpoint();
        ep.ModelId += "-copy";
        ep.DisplayName += " (Copy)";
        var copy = ProviderViewModel.FromEndpoint(ep);
        Providers.Add(copy);
        SelectedProvider = copy;
    }

    public bool HasSelectedProvider => SelectedProvider != null;

    [RelayCommand]
    private void Save()
    {
        // Update AgentConfig
        var config = AgentConfig.Config;
        config.MaxIterations = MaxIterations;
        config.TimeoutMs = TimeoutSeconds * 1000;
        config.Stream = Streaming;
        config.AutoApproveTuiTools = AutoApproveTools;
        if (!string.IsNullOrWhiteSpace(WorkDirectory))
            config.WorkDirectory = WorkDirectory;

        // Update model registry
        var registry = ModelRegistry.Instance;
        registry.Models.Clear();
        foreach (var p in Providers)
            registry.Models.Add(p.ToEndpoint());

        if (!string.IsNullOrWhiteSpace(DefaultModelId))
            registry.DefaultModelId = DefaultModelId;
        else if (Providers.Count > 0)
            registry.DefaultModelId = Providers[0].ModelId;

        // Sync default model back to AgentConfig
        var defaultModel = registry.GetDefaultModel();
        if (defaultModel != null)
        {
            config.Model = defaultModel.ModelId;
            config.HostUrl = defaultModel.HostUrl;
            config.AuthScheme = defaultModel.AuthScheme;
            config.AuthToken = defaultModel.AuthToken;
        }

        // Feature toggles
        McpConfig.Instance.Enabled = McpEnabled;
        RagConfig.Instance.Enabled = RagEnabled;

        AgentConfig.SaveConfig();
        Saved = true;
    }

    [ObservableProperty] private bool _saved;
}
