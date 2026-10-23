using System;
using System.Text;
using Avalonia;
using Avalonia.Data;
using Avalonia.Xaml.Interactivity;
using AvaloniaEdit;

namespace Trebuchet.ViewModels
{
    public class DocumentTextBindingBehavior : Behavior<TextEditor>
    {
        private TextEditor? _textEditor = null;

        public static readonly StyledProperty<string> TextProperty =
            AvaloniaProperty.Register<DocumentTextBindingBehavior, string>(nameof(Text), defaultBindingMode: BindingMode.TwoWay);
            
        public static readonly StyledProperty<bool> AllowScrollBellowDocumentProperty =
            AvaloniaProperty.Register<DocumentTextBindingBehavior, bool>(nameof(Text));

        public string Text
        {
            get => GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }
        
        public bool AllowScrollBellowDocument
        {
            get => GetValue(AllowScrollBellowDocumentProperty);
            set => SetValue(AllowScrollBellowDocumentProperty, value);
        }

        protected override void OnAttached()
        {
            base.OnAttached();

            if (AssociatedObject is not { } textEditor) return;
            _textEditor = textEditor;
            _textEditor.TextChanged += TextChanged;
            _textEditor.Options.HighlightCurrentLine = true;
            _textEditor.Options.CutCopyWholeLine = true;
            _textEditor.Encoding = Encoding.UTF8;
            this.GetObservable(TextProperty).Subscribe(TextPropertyChanged);
            this.GetObservable(AllowScrollBellowDocumentProperty).Subscribe(OnAllowScrollBellowDocumentChanged);
        }

        protected override void OnDetaching()
        {
            base.OnDetaching();

            if (_textEditor == null) return;
            _textEditor.TextChanged -= TextChanged;
        }

        private void TextChanged(object? sender, EventArgs eventArgs)
        {
            if (_textEditor is not { Document: not null }) return;
            Text = _textEditor.Document.Text;
        }

        private void TextPropertyChanged(string? text)
        {
            if (_textEditor is not { Document: not null } || text == null) return;
            var caretOffset = _textEditor.CaretOffset;
            _textEditor.Document.Text = text;
            // A shorter text (other ini tab, empty fallback, reloaded file) threw for a caret behind its end.
            _textEditor.CaretOffset = Math.Min(caretOffset, _textEditor.Document.TextLength);
        }
        
        private void OnAllowScrollBellowDocumentChanged(bool allowScrollBellowDocument)
        {
            if (_textEditor is not { Document: not null }) return;

            _textEditor.Options.AllowScrollBelowDocument = AllowScrollBellowDocument;
        }
    }
}