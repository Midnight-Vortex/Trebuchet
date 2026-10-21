using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Trebuchet.ViewModels;
using tot_gui_lib;

namespace Trebuchet.Windows
{
    /// <summary>
    /// Interaction logic for WorkshopSearch.xaml
    /// </summary>
    public partial class WorkshopSearch : WindowAutoPadding
    {
        public WorkshopSearch()
        {
            InitializeComponent();
        }

        private WorkshopSearchViewModel? _viewModel;

        // This window only. A DataContextProperty.Changed class handler registered in the constructor kept every
        // window ever opened alive through the static property, and on each new window the old handlers subscribed
        // their (closed) windows to PageLoaded of the shared view model again.
        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            SetViewModel(DataContext as WorkshopSearchViewModel);
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            // The view model is shared by the search windows: a closed one must not stay subscribed to it.
            SetViewModel(null);
        }

        private void SetViewModel(WorkshopSearchViewModel? viewModel)
        {
            if (_viewModel is not null)
                _viewModel.PageLoaded -= OnPageLoaded;
            _viewModel = viewModel;
            if (_viewModel is not null)
                _viewModel.PageLoaded += OnPageLoaded;
        }

        private Task OnPageLoaded(object? sender, EventArgs e)
        {
            var scrollViewer = this.FindControl<ScrollViewer>(@"PageScrollViewer");
            scrollViewer?.ScrollToHome();
            return Task.CompletedTask;
        }
    }
}