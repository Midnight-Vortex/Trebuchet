using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Trebuchet.Controls
{
    /// <summary>
    /// Interaction logic for IntBox.xaml
    /// </summary>
    public partial class IntBox : UserControl
    {
        public static readonly StyledProperty<int> MaxValueProperty =  AvaloniaProperty.Register<FloatBox, int>(nameof(MaxValue), int.MaxValue);
        public static readonly StyledProperty<int> MinValueProperty =  AvaloniaProperty.Register<FloatBox, int>(nameof(MinValue), int.MinValue);
        public static readonly StyledProperty<int> ValueProperty =  AvaloniaProperty.Register<FloatBox, int>(nameof(Value));

        private static readonly Regex Regex = new Regex("[^0-9.-]+");

        // Registered once: in the instance constructor every new box added one more handler for all of them.
        static IntBox()
        {
            ValueProperty.Changed.AddClassHandler<IntBox>(OnValueChanged);
        }

        public IntBox()
        {
            InitializeComponent();
        }
        
        public int MaxValue
        {
            get => GetValue(MaxValueProperty);
            set => SetValue(MaxValueProperty, value);
        }
        
        public int MinValue
        {
            get => GetValue(MinValueProperty);
            set => SetValue(MinValueProperty, value);
        }
        
        public int Value
        {
            get => Math.Clamp(GetValue(ValueProperty), MinValue, MaxValue);
            set => SetValue(ValueProperty, Math.Clamp(value, MinValue, MaxValue));
        }
        
        private static void OnValueChanged(IntBox sender, AvaloniaPropertyChangedEventArgs e)
        {
            int value = (int)(e.NewValue ?? 0);
            sender.TextField.Text = value.ToString();
        }
        
        private bool IsTextAllowed(string text)
        {
            return !Regex.IsMatch(text);
        }
        
        
        private void TextField_PreviewLostKeyboardFocus(object? sender, RoutedEventArgs e)
        {
            if (int.TryParse(TextField.Text, out int number))
            {
                Value = number;
                TextField.Text = Value.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                Value = 0;
                TextField.Text = @"0";
            }
        }

        private void TextBox_PreviewTextInput(object? sender, TextInputEventArgs e)
        {
            if (!IsTextAllowed(e.Text ?? string.Empty))
                e.Handled = true;
        }

        private void TextBox_Pasting(object? sender, RoutedEventArgs e)
        {
            if (!IsTextAllowed(TextField.Text ?? string.Empty))
                TextField.Text = Value.ToString(CultureInfo.InvariantCulture);
        }
    }
}