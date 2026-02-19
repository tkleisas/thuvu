using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Mvvm.Controls;

namespace thuvu.Desktop.ViewModels;

/// <summary>
/// Base class for dockable document panels (center area)
/// </summary>
public class DocumentViewModel : Document
{
}

/// <summary>
/// Base class for dockable tool panels (side/bottom areas)
/// </summary>
public class ToolViewModel : Dock.Model.Mvvm.Controls.Tool
{
}
