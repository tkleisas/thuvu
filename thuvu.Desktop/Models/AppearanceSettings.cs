using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace thuvu.Desktop.Models;

/// <summary>
/// Per-panel appearance settings (font, size, colors)
/// </summary>
public class PanelAppearance
{
    public string FontFamily { get; set; } = "";
    public double FontSize { get; set; }
    public string Foreground { get; set; } = "";
    public string Background { get; set; } = "";
}

/// <summary>
/// Global appearance settings for the desktop app, stored in ProjectConfig
/// </summary>
public class AppearanceSettings
{
    public PanelAppearance Editor { get; set; } = new()
    {
        FontFamily = "Cascadia Code, Consolas, Courier New",
        FontSize = 13
    };

    public PanelAppearance Terminal { get; set; } = new()
    {
        FontFamily = "Cascadia Mono, Cascadia Code, Consolas, Courier New",
        FontSize = 14
    };

    public PanelAppearance Chat { get; set; } = new()
    {
        FontFamily = "",
        FontSize = 14
    };
}

/// <summary>
/// Observable singleton that views bind to for live appearance updates.
/// Call Apply() after changing settings to notify all subscribers.
/// </summary>
public class AppearanceService : INotifyPropertyChanged
{
    private static AppearanceService? _instance;
    public static AppearanceService Instance => _instance ??= new AppearanceService();

    private AppearanceSettings _settings = new();

    // Editor
    public string EditorFontFamily => _settings.Editor.FontFamily;
    public double EditorFontSize => _settings.Editor.FontSize;
    public string EditorForeground => _settings.Editor.Foreground;
    public string EditorBackground => _settings.Editor.Background;

    // Terminal
    public string TerminalFontFamily => _settings.Terminal.FontFamily;
    public double TerminalFontSize => _settings.Terminal.FontSize;
    public string TerminalForeground => _settings.Terminal.Foreground;
    public string TerminalBackground => _settings.Terminal.Background;

    // Chat
    public string ChatFontFamily => _settings.Chat.FontFamily;
    public double ChatFontSize => _settings.Chat.FontSize;
    public string ChatForeground => _settings.Chat.Foreground;
    public string ChatBackground => _settings.Chat.Background;

    public void Apply(AppearanceSettings settings)
    {
        _settings = settings;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null)); // notify all
    }

    public AppearanceSettings GetSettings() => _settings;

    public event PropertyChangedEventHandler? PropertyChanged;
}
