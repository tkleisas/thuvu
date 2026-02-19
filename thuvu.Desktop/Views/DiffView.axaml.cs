using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using System.Collections.Generic;

namespace thuvu.Desktop.Views;

public partial class DiffView : UserControl
{
    public static readonly FuncValueConverter<string?, List<DiffLine>> DiffLinesConverter =
        new(diff =>
        {
            var lines = new List<DiffLine>();
            if (string.IsNullOrEmpty(diff)) return lines;

            foreach (var line in diff.Split('\n'))
            {
                IBrush bg;
                if (line.StartsWith("+++") || line.StartsWith("---"))
                    bg = new SolidColorBrush(Color.FromArgb(30, 100, 100, 255));
                else if (line.StartsWith("@@"))
                    bg = new SolidColorBrush(Color.FromArgb(25, 150, 150, 255));
                else if (line.StartsWith('+'))
                    bg = new SolidColorBrush(Color.FromArgb(40, 0, 200, 0));
                else if (line.StartsWith('-'))
                    bg = new SolidColorBrush(Color.FromArgb(40, 200, 0, 0));
                else
                    bg = Brushes.Transparent;

                lines.Add(new DiffLine { Text = line, Background = bg });
            }
            return lines;
        });

    public DiffView()
    {
        InitializeComponent();
    }
}

public class DiffLine
{
    public string Text { get; set; } = string.Empty;
    public IBrush Background { get; set; } = Brushes.Transparent;
}
