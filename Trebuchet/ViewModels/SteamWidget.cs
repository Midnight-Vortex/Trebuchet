using System;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Threading;
using Humanizer;
using ReactiveUI;
using AppResources = Trebuchet.Assets.Resources;
using Trebuchet.Services;
using TrebuchetLib;
using TrebuchetLib.Services;

namespace Trebuchet.ViewModels
{
    public class SteamWidget : ReactiveObject
    {
        private readonly Steam _steam;
        private string _description = string.Empty;
        private bool _isConnected;
        private double _progressBar;
        private bool _canConnect = true;
        private bool _isLoading;
        private string _progressLabel = string.Empty;
        private bool _isIndeterminate;

        public SteamWidget(
            IProgressCallback<DepotDownloader.Progress> progress, 
            Steam steam, 
            TaskBlocker taskBlocker)
        {
            _steam = steam;
            progress.ProgressChanged += OnProgressChanged;

            _steam.Connected += OnSteamConnected;
            _steam.Disconnected += OnSteamDisconnected;

            CancelCommand = ReactiveCommand.Create(() =>
            {
                steam.CancelOperation();
                Description = @$"{AppResources.Cancelling}...";
            });

            var canConnect = this.WhenAnyValue(x => x.CanConnect, x => x.IsConnected, (can, isc) => can && !isc);
            ConnectCommand = ReactiveCommand.CreateFromTask(_steam.Connect, canConnect);

            steam.StatusChanged += OnSteamStatusChanged;
        }

        private void OnSteamStatusChanged(object? sender, SteamStatus e)
        {
            Progress = 0;
            ProgressLabel = string.Empty;
            IsIndeterminate = true;
            switch (e)
            {
                case SteamStatus.UpdatingMods:
                    Description = AppResources.UpdateModsLabel;
                    break;
                case SteamStatus.UpdatingServers:
                    Description = AppResources.UpdateServersLabel;
                    break;
                case SteamStatus.QueryingModDetails:
                case SteamStatus.StandBy:
                default:
                    Description = string.Empty;
                    break;
            }
            IsLoading = e != SteamStatus.StandBy;
        }

        private void OnProgressChanged(object? sender, DepotDownloader.Progress e)
        {
            if (e.IsFile) return;
            Dispatcher.UIThread.Invoke(() =>
            {
                Progress = e.Current / (double)e.Total;
                IsIndeterminate = e.Total == 0;
                ProgressLabel = $@"{((long)e.Current).Bytes().Humanize()}/{((long)e.Total).Bytes().Humanize()}";
            });
        }

        public ReactiveCommand<Unit,Unit> CancelCommand { get; }
        public ReactiveCommand<Unit,Unit> ConnectCommand { get; }

        public string Description
        {
            get => _description;
            set => this.RaiseAndSetIfChanged(ref _description, value);
        }

        public bool IsLoading
        {
            get => _isLoading;
            set => this.RaiseAndSetIfChanged(ref _isLoading, value);
        }

        public bool IsConnected
        {
            get => _isConnected;
            set => this.RaiseAndSetIfChanged(ref _isConnected, value);
        }

        public bool IsIndeterminate
        {
            get => _isIndeterminate;
            set => this.RaiseAndSetIfChanged(ref _isIndeterminate, value);
        }

        public double Progress
        {
            get => _progressBar;
            set => this.RaiseAndSetIfChanged(ref _progressBar, value);
        }

        public string ProgressLabel
        {
            get => _progressLabel;
            set => this.RaiseAndSetIfChanged(ref _progressLabel, value);
        }

        public bool CanConnect
        {
            get => _canConnect;
            set => this.RaiseAndSetIfChanged(ref _canConnect, value);
        }

        private void OnSteamDisconnected(object? sender, EventArgs e)
        {
            Dispatcher.UIThread.Invoke(() =>
            {
                IsConnected = false;
                CanConnect = true;
            });
        }

        private void OnSteamConnected(object? sender, EventArgs e)
        {
            Dispatcher.UIThread.Invoke(() =>
            {
                IsConnected = true;
                CanConnect = true;
            });
        }
    }
}