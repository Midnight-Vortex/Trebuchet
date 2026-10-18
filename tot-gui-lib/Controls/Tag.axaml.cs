#region

using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

#endregion

namespace tot_gui_lib.Controls
{
    /// <summary>
    ///     Interaction logic for Tag.xaml
    /// </summary>
    public partial class Tag : UserControl
    {
        public static readonly StyledProperty<IBrush> ColorProperty =
            AvaloniaProperty.Register<Tag, IBrush>(nameof(Color), defaultValue: Brushes.Transparent);

        public static readonly StyledProperty<bool> CompactProperty =
            AvaloniaProperty.Register<Tag, bool>(nameof(Compact), defaultValue: false);

        public static readonly StyledProperty<string> LabelProperty =
            AvaloniaProperty.Register<Tag, string>(nameof(Label), defaultValue: string.Empty);

        public static readonly StyledProperty<ICommand> TagClickedProperty =
            AvaloniaProperty.Register<Tag, ICommand>(nameof(TagClicked));


        public static readonly StyledProperty<Thickness> TagMarginProperty =
            AvaloniaProperty.Register<Tag, Thickness>(nameof(TagMargin), defaultValue: new Thickness(0));

        public Tag()
        {
            TagMarginProperty.Changed.AddClassHandler<Tag>(OnTagMarginChanged);
            InitializeComponent();
        }

        public IBrush Color
        {
            get => GetValue(ColorProperty);
            set => SetValue(ColorProperty, value);
        }

        public bool Compact
        {
            get => GetValue(CompactProperty);
            set => SetValue(CompactProperty, value);
        }

        public string Label
        {
            get => GetValue(LabelProperty);
            set => SetValue(LabelProperty, value);
        }

        public ICommand TagClicked
        {
            get => GetValue(TagClickedProperty);
            set => SetValue(TagClickedProperty, value);
        }

        public Thickness TagMargin
        {
            get => GetValue(TagMarginProperty);
            set => SetValue(TagMarginProperty, value);
        }

        private static void OnTagMarginChanged(Tag sender, AvaloniaPropertyChangedEventArgs e)
        {
            sender.TagBorder.Margin = sender.TagMargin;
        }
    }
}