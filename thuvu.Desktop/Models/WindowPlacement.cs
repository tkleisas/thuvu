using Avalonia.Controls;

namespace thuvu.Desktop.Models;

/// <summary>Persisted window position, size, and state for restore on next launch.</summary>
public class WindowPlacement
{
    public double Width { get; set; } = 1200;
    public double Height { get; set; } = 800;
    public int X { get; set; }
    public int Y { get; set; }
    public WindowState State { get; set; } = WindowState.Normal;

    // Screen bounds at save time – used to detect if the screen is still connected
    public int ScreenX { get; set; }
    public int ScreenY { get; set; }
    public int ScreenWidth { get; set; }
    public int ScreenHeight { get; set; }
}
