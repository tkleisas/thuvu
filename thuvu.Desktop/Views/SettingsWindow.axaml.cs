using Avalonia.Controls;
using thuvu.Models;

namespace thuvu.Desktop.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        LoadCurrentConfig();
    }

    private void LoadCurrentConfig()
    {
        try
        {
            var config = AgentConfig.Config;
            HostUrlBox.Text = config.HostUrl;
            ModelIdBox.Text = config.Model;
            ContextLengthBox.Value = config.MaxContextLength > 0 ? config.MaxContextLength : 32768;
            MaxIterationsBox.Value = config.MaxIterations > 0 ? config.MaxIterations : 50;
            TimeoutBox.Value = config.TimeoutMs / 1000;
            StreamingCheckBox.IsChecked = config.Stream;
            McpCheckBox.IsChecked = McpConfig.Instance.Enabled;
            RagCheckBox.IsChecked = RagConfig.Instance.Enabled;
        }
        catch { }
    }
}
