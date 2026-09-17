using System;
using Avalonia;
using Avalonia.Xaml.Interactivity;
using AvaloniaEdit;

namespace Trebuchet.ViewModels;

public class ConsoleTextBindingBehavior : Behavior<TextEditor>
{
    private TextEditor? _textEditor;
    private ITextSource? _subscribedSource;

    public static readonly StyledProperty<ITextSource?> TextSourceProperty =
        AvaloniaProperty.Register<ConsoleTextBindingBehavior, ITextSource?>(nameof(TextSource));

    public ITextSource? TextSource
    {
        get => GetValue(TextSourceProperty);
        set => SetValue(TextSourceProperty, value);
    }

    protected override void OnAttached()
    {
        base.OnAttached();

        if (AssociatedObject is not { } textEditor) return;
        _textEditor = textEditor;
        _textEditor.Options.AllowScrollBelowDocument = false;
        BindSource(TextSource);
    }

    protected override void OnDetaching()
    {
        base.OnDetaching();
        BindSource(null);
        _textEditor = null;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextSourceProperty)
            BindSource(change.NewValue as ITextSource);
    }

    private void BindSource(ITextSource? source)
    {
        if (_subscribedSource is { } previous)
        {
            previous.TextAppended -= OnTextAppended;
            previous.TextCleared -= OnTextCleared;
        }
        _subscribedSource = null;
        if (_textEditor is not { Document: not null }) return;
        _textEditor.Clear();
        if (source is null) return;
        _subscribedSource = source;
        source.TextAppended += OnTextAppended;
        source.TextCleared += OnTextCleared;
        _textEditor.AppendText(source.Text);
        if (source.AutoScroll) _textEditor.ScrollToEnd();
    }

    private void OnTextCleared(object? sender, EventArgs e)
    {
        if (_textEditor is not { Document: not null } || !ReferenceEquals(sender, _subscribedSource)) return;
        _textEditor.Clear();
    }

    private void OnTextAppended(object? sender, string text)
    {
        if (_textEditor is not { Document: not null } || _subscribedSource is null
            || !ReferenceEquals(sender, _subscribedSource)) return;
            
        var caretOffset = _textEditor.CaretOffset;
        _textEditor.BeginChange();
        _textEditor.AppendText(text);
        var excess = Math.Max(0, _textEditor.Document.TextLength - _subscribedSource.MaxChar);
        if (excess > 0) _textEditor.Document.Remove(0, excess);
        _textEditor.EndChange();
        _textEditor.CaretOffset = Math.Clamp(caretOffset - excess, 0, _textEditor.Document.TextLength);
        if(_subscribedSource.AutoScroll)
            _textEditor.ScrollToEnd();
    }
}
