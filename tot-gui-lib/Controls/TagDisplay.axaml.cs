#region

using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

#endregion

namespace tot_gui_lib.Controls
{
    /// <summary>
    ///     Interaction logic for TagDisplay.xaml
    /// </summary>
    public partial class TagDisplay : UserControl
    {
        public static readonly StyledProperty<bool> CompactProperty =
            AvaloniaProperty.Register<TagDisplay, bool>(nameof(Compact), defaultValue: false);

        public static readonly StyledProperty<ICommand> TagClickedProperty =
            AvaloniaProperty.Register<TagDisplay, ICommand>(nameof(TagClicked));

        public static readonly StyledProperty<Thickness> TagMarginProperty =
            AvaloniaProperty.Register<TagDisplay, Thickness>(nameof(TagMargin));

        public static readonly StyledProperty<ITag> TagsProperty =
            AvaloniaProperty.Register<TagDisplay, ITag>(nameof(Tags));

        public TagDisplay()
        {
            InitializeComponent();
        }

        public bool Compact
        {
            get => GetValue(CompactProperty);
            set => SetValue(CompactProperty, value);
        }

        public ICommand TagClicked
        {
            get => (ICommand)GetValue(TagClickedProperty);
            set => SetValue(TagClickedProperty, value);
        }

        public Thickness TagMargin
        {
            get => GetValue(TagMarginProperty);
            set => SetValue(TagMarginProperty, value);
        }

        public ObservableCollection<ITag> Tags
        {
            get => (ObservableCollection<ITag>)GetValue(TagsProperty);
            set => SetValue(TagsProperty, value);
        }
    }
}