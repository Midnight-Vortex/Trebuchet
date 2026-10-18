#region

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

#endregion

namespace tot_gui_lib.Controls
{
    /// <summary>
    ///     Interaction logic for LabelSearcher.xaml
    /// </summary>
    public partial class TagSearcher : UserControl
    {
        public static readonly StyledProperty<double> DeployedWidthProperty =
            AvaloniaProperty.Register<TagSearcher, double>(nameof(DeployedWidth));

        public static readonly StyledProperty<string> ButtonClassProperty =
            AvaloniaProperty.Register<TagSearcher, string>(nameof(ButtonClass));

        public static readonly StyledProperty<int> MinimumCharactersProperty =
            AvaloniaProperty.Register<TagSearcher, int>(nameof(MinimumCharacters), defaultValue: 2);

        public static readonly StyledProperty<IEnumerable<ITag>> SelectedTagsProperty =
            AvaloniaProperty.Register<TagSearcher, IEnumerable<ITag>>(nameof(SelectedTags), defaultValue: []);

        public static readonly StyledProperty<ICommand> TagClickedProperty =
            AvaloniaProperty.Register<TagSearcher, ICommand>(nameof(TagClicked));

        public static readonly StyledProperty<Thickness> TagMarginProperty =
            AvaloniaProperty.Register<TagSearcher, Thickness>("TagMargin", defaultValue: new Thickness(0, 0, 0, 6));

        public static readonly StyledProperty<ICollection<ITag>> TagsProperty =
            AvaloniaProperty.Register<TagSearcher, ICollection<ITag>>(nameof(Tags));

        public static readonly StyledProperty<bool> ResultsOpenProperty =
            AvaloniaProperty.Register<TagSearcher, bool>(nameof(ResultsOpen));

        public static readonly StyledProperty<string> SearchContentProperty =
            AvaloniaProperty.Register<TagSearcher, string>(nameof(SearchContent));

        public static readonly StyledProperty<ICollection<ITag>> ResultsProperty =
            AvaloniaProperty.Register<TagSearcher, ICollection<ITag>>(nameof(Results));


        public TagSearcher()
        {
            SelectedTagsProperty.Changed.AddClassHandler<TagSearcher>(OnSelectionChanged);
            MinimumCharactersProperty.Changed.AddClassHandler<TagSearcher>(OnMinimumChanged);
            TagsProperty.Changed.AddClassHandler<TagSearcher>(OnLabelsChanged);
            SearchContentProperty.Changed.AddClassHandler<TagSearcher>(OnSearchContentChanged);
            InitializeComponent();
            SearchContent = "";
            SearchField.GotFocus += OnGainKeyboardFocus;
            SearchField.LostFocus += OnLostKeyboardFocus;

            ReturnHotkey = new Command(OnReturn, OnCanReturn);
            ClearCommand = new Command(OnClearField, _ => true);
            StartSearch = new Command(OnStartSearch, _ => true);
            InternalTagClicked = new Command(OnInternalTagClicked, _ => true);
            PropertyChanged += OnPropertyChanged;
            SearchField.IsVisible = false;
            IconTag.Opacity = 1.0;
        }

        public ICommand ClearCommand { get; private set; }

        public bool Compact => false;

        public double DeployedWidth
        {
            get => GetValue(DeployedWidthProperty);
            set => SetValue(DeployedWidthProperty, value);
        }

        public ICommand InternalTagClicked { get; private set; }

        public int MinimumCharacters
        {
            get => GetValue(MinimumCharactersProperty);
            set => SetValue(MinimumCharactersProperty, value);
        }

        public ICollection<ITag> Results
        {
            get => GetValue(ResultsProperty);
            set => SetValue(ResultsProperty, value);
        }

        public bool ResultsOpen
        {
            get => GetValue(ResultsOpenProperty);
            set => SetValue(ResultsOpenProperty, value);
        }

        public ICommand ReturnHotkey { get; private set; }

        public string SearchContent
        {
            get => GetValue(SearchContentProperty);
            set => SetValue(SearchContentProperty, value);
        }

        public IEnumerable<ITag> SelectedTags
        {
            get => GetValue(SelectedTagsProperty);
            set => SetValue(SelectedTagsProperty, value);
        }

        public ICommand StartSearch { get; private set; }

        public ICommand TagClicked
        {
            get => GetValue(TagClickedProperty);
            set => SetValue(TagClickedProperty, value);
        }

        public string ButtonClass
        {
            get => GetValue(ButtonClassProperty);
            set => SetValue(ButtonClassProperty, value);
        }

        public Thickness TagMargin
        {
            get => GetValue(TagMarginProperty);
            set => SetValue(TagMarginProperty, value);
        }

        public ICollection<ITag> Tags
        {
            get => GetValue(TagsProperty);
            set => SetValue(TagsProperty, value);
        }

        private void OnSearchContentChanged(TagSearcher sender, AvaloniaPropertyChangedEventArgs e)
        {
            HandleResults();
            ResultsOpen = SearchContent.Length >= MinimumCharacters || SearchContent == "*";
        }

        private void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property.Name == nameof(IsVisible) && IsVisible == false)
            {
                SearchContent = "";
            }
        }

        public static IEnumerable<T> FindVisualChildren<T>(Visual depObj) where T : Visual
        {
            foreach (var child in depObj.GetVisualChildren())
            {
                if (child is T visual)
                {
                    yield return visual;
                }

                foreach (var childOfChild in FindVisualChildren<T>(child))
                {
                    yield return childOfChild;
                }
            }
        }

        private void OnLabelsChanged(TagSearcher sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.NewValue is not ObservableCollection<ITag>) return;
            HandleResults();
            ((Command)sender.ReturnHotkey).OnCanExecuteChanged();
        }

        private void OnMinimumChanged(TagSearcher sender, EventArgs e)
        {
            Dispatcher.UIThread.Invoke(() => { sender.SearchContent = sender.SearchContent; });
        }

        private void OnSelectionChanged(TagSearcher sender, EventArgs e)
        {
            Dispatcher.UIThread.Invoke(() =>
            {
                HandleResults();
                ((Command)sender.ReturnHotkey).OnCanExecuteChanged();
            });
        }

        private bool FilterSearch(ITag tag)
        {
            if (SearchContent == "*")
                return true;
            if (SearchContent.Length < MinimumCharacters)
                return false;
            return tag.Name.Contains(SearchContent, StringComparison.CurrentCultureIgnoreCase) &&
                   !SelectedTags.Contains(tag);
        }

        private void HandleResults()
        {
            var list = new List<ITag>();
            foreach (var tag in Tags)
            {
                if (FilterSearch(tag))
                    list.Add(tag);
            }

            list.Sort(new LabelOrdering());

            Results = list;
        }

        private bool OnCanReturn(object? arg)
        {
            return Results.Count == 1;
        }

        private void OnClearField(object? obj)
        {
            SearchContent = "";
        }

        private void OnGainKeyboardFocus(object? sender, FocusChangedEventArgs e)
        {
        }

        private void OnInternalTagClicked(object? obj)
        {
            if (obj != null)
            {
                ClearCommand.Execute(obj);
                TagClicked.Execute(obj);
            }
        }

        private void OnLostKeyboardFocus(object? sender, RoutedEventArgs e)
        {
            ClearCommand.Execute(this);
            SearchButton.IsVisible = true;
            SearchField.IsVisible = false;
            IconTag.Opacity = 1.0;
        }

        private void OnReturn(object? obj)
        {
            if (Results.Count == 1 && TagClicked.CanExecute(Results.First()))
            {
                TagClicked.Execute(Results.First());
                ClearCommand.Execute(this);
            }
        }

        private void OnStartSearch(object? obj)
        {
            SearchButton.IsVisible = false;
            SearchField.IsVisible = true;
            IconTag.Opacity = 0.4;
            SearchField.Focus();
        }
    }
}