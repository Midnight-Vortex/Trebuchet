using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Trebuchet.Controls;

/// <summary>
/// Masked input for a secret. The typed text is never bound to a view model: on submit (button or Enter) it is
/// cleared from the box first and only then handed to <see cref="SubmitCommand"/> as the parameter. It is also cleared
/// when the box leaves the screen, so a half typed secret does not stay in memory.
/// </summary>
public partial class SecretBox : UserControl
{
    public static readonly StyledProperty<ICommand?> SubmitCommandProperty =
        AvaloniaProperty.Register<SecretBox, ICommand?>(nameof(SubmitCommand));

    public static readonly StyledProperty<string?> WatermarkProperty =
        AvaloniaProperty.Register<SecretBox, string?>(nameof(Watermark));

    public SecretBox()
    {
        InitializeComponent();
        SubmitButton.Click += OnSubmitClick;
        Input.KeyDown += OnInputKeyDown;
    }

    public ICommand? SubmitCommand
    {
        get => GetValue(SubmitCommandProperty);
        set => SetValue(SubmitCommandProperty, value);
    }

    public string? Watermark
    {
        get => GetValue(WatermarkProperty);
        set => SetValue(WatermarkProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WatermarkProperty)
            Input.PlaceholderText = change.GetNewValue<string?>();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Input.Clear();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnSubmitClick(object? sender, RoutedEventArgs e) => Submit();

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Submit();
    }

    private void Submit()
    {
        var text = Input.Text ?? string.Empty;
        Input.Clear();
        if (text.Length == 0) return;
        var command = SubmitCommand;
        if (command?.CanExecute(text) == true)
            command.Execute(text);
    }
}
