using Avalonia;
using Avalonia.Xaml.Interactivity;
using AvaloniaEdit;

namespace thuvu.Desktop.Behaviors;

/// <summary>
/// Enables two-way binding of AvaloniaEdit TextEditor.Text via a Behavior.
/// See: https://github.com/AvaloniaUI/AvaloniaEdit/wiki/MVVM
/// </summary>
public class DocumentTextBindingBehavior : Behavior<TextEditor>
{
    private TextEditor? _textEditor;

    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<DocumentTextBindingBehavior, string>(
            nameof(Text), defaultValue: string.Empty);

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    protected override void OnAttached()
    {
        base.OnAttached();
        if (AssociatedObject is TextEditor textEditor)
        {
            _textEditor = textEditor;
            _textEditor.TextChanged += OnTextChanged;
            this.GetObservable(TextProperty).Subscribe(OnTextPropertyChanged);
        }
    }

    protected override void OnDetaching()
    {
        base.OnDetaching();
        if (_textEditor != null)
            _textEditor.TextChanged -= OnTextChanged;
    }

    private void OnTextChanged(object? sender, EventArgs e)
    {
        if (_textEditor?.Document != null)
            Text = _textEditor.Document.Text;
    }

    private void OnTextPropertyChanged(string? text)
    {
        if (_textEditor?.Document != null && text != null)
        {
            var caretOffset = _textEditor.CaretOffset;
            _textEditor.Document.Text = text;
            if (caretOffset <= text.Length)
                _textEditor.CaretOffset = caretOffset;
        }
    }
}
