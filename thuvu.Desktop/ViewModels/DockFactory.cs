using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;

namespace thuvu.Desktop.ViewModels;

/// <summary>
/// Factory that creates the default dock layout
/// </summary>
public class DockFactory : Factory
{
    private IRootDock? _rootDock;

    public override IRootDock CreateLayout()
    {
        var chat = new ChatViewModel();
        var fileTree = new FileTreeViewModel();
        var terminal = new TerminalViewModel();

        var documentDock = new DocumentDock
        {
            Id = "DocumentDock",
            Title = "Documents",
            Proportion = 0.65,
            IsCollapsable = false,
            VisibleDockables = CreateList<IDockable>(chat),
            ActiveDockable = chat,
            CanCreateDocument = false
        };

        var fileTreeTool = new ToolDock
        {
            Id = "FileTreeDock",
            Title = "Explorer",
            Proportion = 0.20,
            VisibleDockables = CreateList<IDockable>(fileTree),
            ActiveDockable = fileTree,
            Alignment = Alignment.Left
        };

        var terminalTool = new ToolDock
        {
            Id = "TerminalDock",
            Title = "Terminal",
            Proportion = 0.25,
            VisibleDockables = CreateList<IDockable>(terminal),
            ActiveDockable = terminal,
            Alignment = Alignment.Bottom
        };

        // Left panel | (Center documents / Bottom terminal)
        var centerWithTerminal = new ProportionalDock
        {
            Id = "CenterWithTerminal",
            Orientation = Orientation.Vertical,
            Proportion = 0.80,
            VisibleDockables = CreateList<IDockable>(
                documentDock,
                new ProportionalDockSplitter(),
                terminalTool
            )
        };

        var mainLayout = new ProportionalDock
        {
            Id = "MainLayout",
            Orientation = Orientation.Horizontal,
            VisibleDockables = CreateList<IDockable>(
                fileTreeTool,
                new ProportionalDockSplitter(),
                centerWithTerminal
            )
        };

        _rootDock = CreateRootDock();
        _rootDock.Id = "Root";
        _rootDock.Title = "Root";
        _rootDock.IsCollapsable = false;
        _rootDock.VisibleDockables = CreateList<IDockable>(mainLayout);
        _rootDock.ActiveDockable = mainLayout;
        _rootDock.DefaultDockable = mainLayout;

        return _rootDock;
    }

    public override void InitLayout(IDockable layout)
    {
        ContextLocator = new Dictionary<string, Func<object?>>
        {
            ["Chat"] = () => layout,
            ["FileTree"] = () => layout,
            ["Terminal"] = () => layout,
        };

        DockableLocator = new Dictionary<string, Func<IDockable?>>
        {
        };

        HostWindowLocator = new Dictionary<string, Func<IHostWindow?>>
        {
            [nameof(IDockWindow)] = () => new HostWindow()
        };

        base.InitLayout(layout);
    }
}
