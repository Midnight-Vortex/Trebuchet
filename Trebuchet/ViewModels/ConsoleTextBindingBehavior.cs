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
        _textEditor.DocumentChanged += OnDocumentChanged;
        DisableUndo();
        BindSource(TextSource);
    }

    protected override void OnDetaching()
    {
        base.OnDetaching();
        BindSource(null);
        if (_textEditor is not null) _textEditor.DocumentChanged -= OnDocumentChanged;
        _textEditor = null;
    }

    private void OnDocumentChanged(object? sender, EventArgs e) => DisableUndo();

    /// <summary>
    /// Read only log view: no undo history. AvaloniaEdit's undo stack is unlimited by default and kept every
    /// appended and trimmed text, so the console character limit freed nothing and a console left open on a busy
    /// server grew by tens to hundreds of MB a day until out of memory.
    /// </summary>
    private void DisableUndo()
    {
        if (_textEditor?.Document is { } document)
            document.UndoStack.SizeLimit = 0;
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
        DisableUndo();
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
