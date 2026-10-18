using System;
using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using ReactiveUI;
using tot_lib;
using Trebuchet.Assets;
using Trebuchet.Services;
using Trebuchet.ViewModels.InnerContainer;
using TrebuchetLib;
using TrebuchetLib.Processes;

namespace Trebuchet.ViewModels
{
    public class ServerInstanceProfileEventArgs(int instance, ServerProfileRef selection) : EventArgs
    {
        public int Instance { get; } = instance;
        public ServerProfileRef Selection { get; } = selection;
    }
    
    public class ServerInstanceModListEventArgs(int instance, IPRefWithModList selection) : EventArgs
    {
        public int Instance { get; } = instance;
        public IPRefWithModList Selection { get; } = selection;
    }
    
    public sealed class ServerInstanceDashboard : ReactiveObject
    {
        private readonly DialogueBox _box;
        private ProcessState _lastState;
        private bool _canClose;
        private bool _canKill;
        private bool _canLaunch = true;
        private bool _canUseDashboard;
        private List<ModListRefViewModel> _modlists = [];
        private bool _processRunning;
        private List<ServerProfileRef> _profiles = [];
        private ModListRefViewModel? _selectedModlist;
        private ServerProfileRef? _selectedProfile;
        private bool _updateNeeded = false;
        private bool _sequenceRunning;
        private bool _sequenceIndeterminate;
        private double _sequenceProgress;
        private bool _countdownRunning;
        private bool _automaticRestartsPaused;
        private string _resumeRestartsText = string.Empty;

        public ServerInstanceDashboard(int instance, IProcessStats stats, TaskBlocker blocker, DialogueBox box)
        {
            _box = box;
            Instance = instance;
            ProcessStats = stats;
            
            KillCommand = ReactiveCommand.CreateFromTask(OnKilled, this.WhenAnyValue(x => x.CanKill));
            CanKill = false;
            
            CloseCommand = ReactiveCommand.Create(OnClose, this.WhenAnyValue(x => x.CanClose));
            CanClose = false;

            RestartCommand = ReactiveCommand.CreateFromTask(OnRestartClicked, this.WhenAnyValue(x => x.CanClose));
            PauseRestartsCommand = ReactiveCommand.CreateFromTask(OnPauseRestartsClicked, this.WhenAnyValue(x => x.CanClose));
            ResumeRestartsCommand = ReactiveCommand.CreateFromTask(OnResumeRestartsClicked);
            CopyConnectionCommand = ReactiveCommand.CreateFromTask(OnCopyConnectionClicked);

            var canBlockLaunch = blocker.WhenAnyValue(x => x.CanLaunch);
            var canDownloadMods = blocker.WhenAnyValue(x => x.CanDownloadMods);
            var canLaunch = this.WhenAnyValue(x => x.CanLaunch).CombineLatest(canBlockLaunch, (f,s) => f && s);
            LaunchCommand = ReactiveCommand.CreateFromTask(OnLaunched, canLaunch.StartWith(true));
            UpdateModsCommand = ReactiveCommand.CreateFromTask(OnModUpdate, canDownloadMods);

            this.WhenAnyValue(x => x.SelectedModlist)
                .InvokeCommand(ReactiveCommand.CreateFromTask<ModListRefViewModel?>(OnModlistSelected));
            this.WhenAnyValue(x => x.SelectedProfile)
                .InvokeCommand(ReactiveCommand.CreateFromTask<ServerProfileRef?>(OnProfileSelected));
        }

        public event AsyncEventHandler<int>? LaunchClicked;
        public event AsyncEventHandler<int>? KillClicked;
        public event AsyncEventHandler<int>? CloseClicked;
        public event AsyncEventHandler<int>? RestartClicked;
        public event AsyncEventHandler<int>? PauseRestartsClicked;
        public event AsyncEventHandler<int>? ResumeRestartsClicked;
        public event AsyncEventHandler<int>? CopyConnectionClicked;
        public event AsyncEventHandler<ServerInstanceModListEventArgs>? ModlistSelected;
        public event AsyncEventHandler<ServerInstanceProfileEventArgs>? ProfileSelected;
        public event AsyncEventHandler<int>? UpdateClicked;

        public bool CanClose
        {
            get => _canClose;
            set => this.RaiseAndSetIfChanged(ref _canClose, value);
        }

        public bool CanKill
        {
            get => _canKill;
            set => this.RaiseAndSetIfChanged(ref _canKill, value);
        }

        public bool CanLaunch
        {
            get => _canLaunch;
            set => this.RaiseAndSetIfChanged(ref _canLaunch, value);
        }

        public bool CanUseDashboard
        {
            get => _canUseDashboard;
            set => this.RaiseAndSetIfChanged(ref _canUseDashboard, value);
        }

        public List<ModListRefViewModel> Modlists
        {
            get => _modlists;
            set => this.RaiseAndSetIfChanged(ref _modlists, value);
        }

        public bool ProcessRunning
        {
            get => _processRunning;
            set => this.RaiseAndSetIfChanged(ref _processRunning, value);
        }

        public bool SequenceRunning
        {
            get => _sequenceRunning;
            set => this.RaiseAndSetIfChanged(ref _sequenceRunning, value);
        }

        public bool SequenceIndeterminate
        {
            get => _sequenceIndeterminate;
            set => this.RaiseAndSetIfChanged(ref _sequenceIndeterminate, value);
        }

        public double SequenceProgress
        {
            get => _sequenceProgress;
            set => this.RaiseAndSetIfChanged(ref _sequenceProgress, value);
        }

        /// <summary>A stop or restart countdown runs: the pause button cancels it.</summary>
        public bool CountdownRunning
        {
            get => _countdownRunning;
            set => this.RaiseAndSetIfChanged(ref _countdownRunning, value);
        }

        public bool AutomaticRestartsPaused
        {
            get => _automaticRestartsPaused;
            set => this.RaiseAndSetIfChanged(ref _automaticRestartsPaused, value);
        }

        public string ResumeRestartsText
        {
            get => _resumeRestartsText;
            set => this.RaiseAndSetIfChanged(ref _resumeRestartsText, value);
        }

        public List<ServerProfileRef> Profiles
        {
            get => _profiles;
            set => this.RaiseAndSetIfChanged(ref _profiles, value);
        }

        public ModListRefViewModel? SelectedModlist
        {
            get => _selectedModlist;
            set => this.RaiseAndSetIfChanged(ref _selectedModlist, value);
        }

        public ServerProfileRef? SelectedProfile
        {
            get => _selectedProfile;
            set => this.RaiseAndSetIfChanged(ref _selectedProfile, value);
        }

        public bool UpdateNeeded
        {
            get => _updateNeeded;
            set => this.RaiseAndSetIfChanged(ref _updateNeeded, value);
        }

        public IProcessStats ProcessStats { get; }

        public int Instance { get; }
        public ReactiveCommand<Unit,Unit> CloseCommand { get; }
        public ReactiveCommand<Unit,Unit> RestartCommand { get; }
        public ReactiveCommand<Unit,Unit> PauseRestartsCommand { get; }
        public ReactiveCommand<Unit,Unit> ResumeRestartsCommand { get; }
        public ReactiveCommand<Unit,Unit> CopyConnectionCommand { get; }
        public ReactiveCommand<Unit,Unit> KillCommand { get; }
        public ReactiveCommand<Unit,Unit> LaunchCommand { get; }
        public ReactiveCommand<Unit,Unit> UpdateModsCommand { get; private set; }

        public async Task ProcessRefresh(IConanProcess? process, bool refreshStats)
        {
            var state = process?.State ?? ProcessState.STOPPED;
            if (_lastState.IsRunning() && !state.IsRunning())
                OnProcessTerminated();
            else if (!_lastState.IsRunning() && state.IsRunning() && process is not null)
                OnProcessStarted(process, refreshStats);

            if (state == ProcessState.FAILED)
                await OnProcessFailed();
            else if (ProcessRunning && process is not null)
                ProcessStats.Details = process;

            _lastState = state;
        }

        public void SetSequenceProgress(SequenceProgress progress)
        {
            SequenceRunning = progress.Total > 0;
            if(progress.Total > 0)
                SequenceProgress = (double)progress.Current / progress.Total;
            SequenceIndeterminate = progress.Current == 0;
        }

        public void SetAutomaticRestarts(bool countdownRunning, DateTime? pausedUntilUtc)
        {
            CountdownRunning = countdownRunning;
            AutomaticRestartsPaused = pausedUntilUtc is not null;
            if (pausedUntilUtc is not { } until) return;
            var local = until.ToLocalTime();
            ResumeRestartsText = string.Format(Resources.ResumeRestarts,
                local.ToString(local.Date == DateTime.Today ? "t" : "g"));
        }

        private async Task OnPauseRestartsClicked()
        {
            if (PauseRestartsClicked is not null)
                await PauseRestartsClicked.Invoke(this, Instance);
        }

        private async Task OnResumeRestartsClicked()
        {
            if (ResumeRestartsClicked is not null)
                await ResumeRestartsClicked.Invoke(this, Instance);
        }

        private async Task OnKillClicked()
        {
            if (KillClicked is not null)
                await KillClicked.Invoke(this, Instance);
        }

        private async Task OnRestartClicked()
        {
            if (RestartClicked is not null)
                await RestartClicked.Invoke(this, Instance);
        }

        private async Task OnCopyConnectionClicked()
        {
            if (CopyConnectionClicked is not null)
                await CopyConnectionClicked.Invoke(this, Instance);
        }

        private async Task OnLaunchClicked()
        {
            if (LaunchClicked is not null)
                await LaunchClicked.Invoke(this, Instance);
        }

        private async Task OnProfileSelected(ServerProfileRef? profile)
        {
            if (ProfileSelected is not null && profile is not null)
                await ProfileSelected.Invoke(this, new ServerInstanceProfileEventArgs(Instance, profile));
        }

        private async Task OnModlistSelected(ModListRefViewModel? modlist)
        {
            if (ModlistSelected is not null && modlist is not null)
                await ModlistSelected.Invoke(this, new ServerInstanceModListEventArgs(Instance, modlist.ModList));
        }

        private async Task OnUpdateClicked()
        {
            if (UpdateClicked is not null)
                await UpdateClicked.Invoke(this, Instance);
        }

        private void OnClose()
        {
            CloseClicked?.Invoke(this, Instance);
        }

        private async Task OnKilled()
        {
            await OnKillClicked();
        }

        private async Task OnLaunched()
        {
            if (!CanUseDashboard) return;
            await OnLaunchClicked();
        }

        private async Task OnModUpdate()
        {
            if (SelectedModlist is null) return;
            await OnUpdateClicked();
        }

        private async Task OnProcessFailed()
        {
            CanKill = false;
            CanClose = false;
            CanLaunch = true;
            await _box.OpenErrorAsync(Resources.ServerFailedStart, Resources.ServerFailedStartText);
        }

        private void OnProcessStarted(IConanProcess details, bool refreshProcess)
        {
            CanLaunch = false;
            CanKill = true;
            CanClose = true;

            ProcessRunning = true;
            if(refreshProcess)
                ProcessStats.Details = details;
        }

        private void OnProcessTerminated()
        {
            ProcessStats.Details = ConanProcess.Empty;
            CanKill = false;
            CanClose = false;
            CanLaunch = true;
            ProcessRunning = false;
        }
    }
}