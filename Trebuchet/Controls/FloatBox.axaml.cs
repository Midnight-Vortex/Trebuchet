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
    /// Interaction logic for FloatBox.xaml
    /// </summary>
    public partial class FloatBox : UserControl
    {
        public static readonly StyledProperty<float> MaxValueProperty =  AvaloniaProperty.Register<FloatBox, float>(nameof(MaxValue), float.MaxValue);
        public static readonly StyledProperty<float> MinValueProperty =  AvaloniaProperty.Register<FloatBox, float>(nameof(MinValue), float.MinValue);
        public static readonly StyledProperty<float> ValueProperty =  AvaloniaProperty.Register<FloatBox, float>(nameof(Value));

        private static readonly Regex Regex = new Regex("[^0-9.-]+");

        // Registered once: in the instance constructor every new box added one more handler for all of them.
        static FloatBox()
        {
            ValueProperty.Changed.AddClassHandler<FloatBox>(OnValueChanged);
        }

        public FloatBox()
        {
            InitializeComponent();
        }
        
        public float MaxValue
        {
            get => GetValue(MaxValueProperty);
            set => SetValue(MaxValueProperty, value);
        }
        
        public float MinValue
        {
            get => GetValue(MinValueProperty);
            set => SetValue(MinValueProperty, value);
        }
        
        public float Value
        {
            get => MathF.Min(MaxValue, MathF.Max(MinValue, GetValue(ValueProperty)));
            set => SetValue(ValueProperty, MathF.Min(MaxValue, MathF.Max(MinValue, value)));
        }
        
        private static void OnValueChanged(FloatBox sender, AvaloniaPropertyChangedEventArgs e)
        {
            float value = (float)(e.NewValue ?? 0);
            sender.TextField.Text = value.ToString(CultureInfo.InvariantCulture);
        }
        
        private bool IsTextAllowed(string text)
        {
            return !Regex.IsMatch(text);
        }
        




        private void TextField_PreviewLostKeyboardFocus(object? sender, RoutedEventArgs e)
        {
            if (float.TryParse(TextField.Text, out float number))
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
