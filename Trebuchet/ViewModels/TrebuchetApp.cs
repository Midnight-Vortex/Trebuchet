using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using tot_lib;
using Trebuchet.Services;
using Trebuchet.ViewModels.InnerContainer;
using Trebuchet.ViewModels.Panels;
using TrebuchetLib;
using TrebuchetLib.Services;

namespace Trebuchet.ViewModels;

public sealed class TrebuchetApp : ReactiveObject, IDisposable
{
    public static string VersionHeader => ProcessUtil.GetStringVersion();
    
    public TrebuchetApp(
        UIConfig uiConfig,
        AppSetup setup,
        SteamWidget steamWidget,
        DialogueBox dialogueBox,
        Operations operations,
        IEnumerable<IPanel> panels,
        ILogger<TrebuchetApp> logger)
    {
        _logger = logger;
        _uiConfig = uiConfig;
        _operations = operations;
        _panels = panels.ToList();
        SteamWidget = steamWidget;
        DialogueBox = dialogueBox;
        WindowTitle = $"Tot!Trebuchet — {Constants.GetEditionDisplayName(setup.Edition)}";
        EditionLabel = Constants.GetEditionDisplayName(setup.Edition);
        
        foreach (var panel in _panels)
        {
            var command = new PanelTab(panel);
            command.PanelSelected += OnTabClicked;
            
            if(panel is IRefreshingPanel refreshing)
                refreshing.RequestRefresh += (_,_) => RefreshPanels();
            
            if(panel is IBottomPanel)
                BottomPanels.Add(command);
            else
                TopPanels.Add(command);
        }

        _activePanel = BottomPanels.First(x => x.Panel.CanBeOpened);

        FoldedMenu = uiConfig.FoldedMenu;
        // 2s is enough for process attach / dashboard; halves WMI + panel tick load vs 1s.
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, OnTimerTick);

        Start();
    }
    
    private readonly ILogger<TrebuchetApp> _logger;
    private readonly UIConfig _uiConfig;
    private readonly Operations _operations;
    private int _tickFailures;
    // Longest wait for the ticks of one timer round. A slower panel keeps running and is not ticked again
    // until it completed (see TickPanels).
    private static readonly TimeSpan TickTimeout = TimeSpan.FromMinutes(2);
    private readonly Dictionary<ITickingPanel, Task> _hungTicks = [];
    private readonly Dictionary<ITickingPanel, DateTime> _hungSinceUtc = [];
    // A tick that stays hung this long stops the automation for good (no crash or scheduled restarts): Trebuchet
    // restarts itself then, the servers keep running and are attached again.
    private static readonly TimeSpan HungTickRestart = TimeSpan.FromMinutes(10);
    private readonly List<IPanel> _panels;
    private readonly DispatcherTimer _timer;
    private PanelTab _activePanel;
    private bool _foldedMenu;

    public bool FoldedMenu
    {
        get => _foldedMenu;
        set => this.RaiseAndSetIfChanged(ref _foldedMenu, value);
    }
        
    public PanelTab ActivePanel
    {
        get => _activePanel;
        set => this.RaiseAndSetIfChanged(ref _activePanel, value);
    }

    public ObservableCollection<PanelTab> TopPanels { get; } = [];
    public ObservableCollection<PanelTab> BottomPanels { get; } = [];

    public SteamWidget SteamWidget { get; }
        
    public DialogueBox DialogueBox { get; }

    public string WindowTitle { get; }

    public string EditionLabel { get; }

    private async Task OnTabClicked(object? sender, PanelTab tab)
    {
        ActivePanel.Active = false;
        ActivePanel = tab;
        ActivePanel.Active = true;
        if (ActivePanel.Panel is IRefreshablePanel refreshable)
            await refreshable.RefreshPanel();
        if (ActivePanel.Panel is IDisplablePanel displayable)
            await displayable.DisplayPanel();
    }
    
    private async void OnTimerTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        List<(string panel, Exception error)> failures;
        try
        {
            failures = await TickPanels();
        }
        catch (Exception ex)
        {
            failures = [(nameof(TrebuchetApp), ex)];
        }

        var hung = _hungSinceUtc.FirstOrDefault(x => DateTime.UtcNow - x.Value > HungTickRestart);
        if (hung.Key is not null)
        {
            await App.HandleAppCrash(new TimeoutException(
                $@"Tick of {hung.Key.GetType().Name} hangs since {hung.Value:u}, the automation stopped"));
            return;
        }

        if (failures.Count == 0)
            _tickFailures = 0;
        else
        {
            // One failed refresh (process exiting mid-tick, locked file, ...) must not close the application.
            _tickFailures++;
            foreach (var (panel, error) in failures)
                _logger.LogError(error, @"Tick of {Panel} failed ({Count} in a row)", panel, _tickFailures);
            if (_tickFailures >= 30)
            {
                await App.HandleAppCrash(failures[0].error);
                return;
            }
        }
        _timer.Start();
    }

    /// <summary>
    /// Tick every panel and wait for them, at most <see cref="TickTimeout"/>. Returns the panels that failed.
    /// </summary>
    private async Task<List<(string panel, Exception error)>> TickPanels()
    {
        var ticks = new List<(ITickingPanel panel, Task task)>();
        foreach (var panel in _panels.OfType<ITickingPanel>())
        {
            // A panel whose previous tick is still running (hung await, e.g. a WMI query that never returns) is
            // skipped: never two Launcher ticks at the same time. The other panels keep ticking meanwhile.
            if (_hungTicks.TryGetValue(panel, out var hung))
            {
                if (!hung.IsCompleted) continue;
                _hungTicks.Remove(panel);
                _hungSinceUtc.Remove(panel);
                _logger.LogWarning(hung.Exception, @"Tick of {Panel} completed late ({Status}), ticking it again",
                    panel.GetType().Name, hung.Status);
            }
            ticks.Add((panel, StartPanelTick(panel)));
        }
        if (ticks.Count == 0) return [];

        // Watchdog: a tick that never completed used to stop the timer for good (no crash restarts, no scheduled
        // restarts, frozen dashboard) while the window still looked alive. Now the timer goes on after the timeout
        // and the hung tick is kept (not started again) until it completes.
        var all = Task.WhenAll(ticks.Select(x => x.task));
        // Errors are read per panel below; this keeps the combined task from reporting them again as unobserved.
        _ = all.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        using var timeout = new CancellationTokenSource();
        var completed = await Task.WhenAny(all, Task.Delay(TickTimeout, timeout.Token));
        await timeout.CancelAsync();
        if (completed != all)
        {
            foreach (var (panel, task) in ticks.Where(x => !x.task.IsCompleted))
            {
                _hungTicks[panel] = task;
                _hungSinceUtc[panel] = DateTime.UtcNow - TickTimeout;
                _logger.LogError(@"Tick of {Panel} did not complete within {Timeout}, it is skipped until it does",
                    panel.GetType().Name, TickTimeout);
            }
        }

        return ticks
            .Where(x => x.task.IsFaulted)
            .Select(x => (x.panel.GetType().Name, x.task.Exception!.GetBaseException()))
            .Where(x => x.Item2 is not OperationCanceledException)
            .ToList();
    }

    /// <summary>
    /// A synchronous throw in one panel's TickPanel (before its first await) must not prevent the ticks of the
    /// panels after it, e.g. the console panel ticking before the dashboard (Launcher): it becomes a failed task.
    /// </summary>
    private static Task StartPanelTick(ITickingPanel panel)
    {
        try
        {
            return panel.TickPanel();
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }
    }

    private async void Start()
    {
        try
        {
            _timer.Start();
            _activePanel.Active = true;
            if(_activePanel.Panel is IDisplablePanel displayed) 
                await displayed.DisplayPanel(); 
            await RefreshActivePanel();
            await StartActions();
        }
        catch (OperationCanceledException){}
        catch (Exception ex)
        {
            // Not the crash path: a restart runs the same start actions again and fails the same way, which only
            // used up the automatic crash restarts (then the modal crash window). The tick timer is already running
            // (servers stay watched), so log and show the error instead.
            _logger.LogError(ex, @"Start actions failed");
            try
            {
                await DialogueBox.OpenErrorAsync(ex.Message);
            }
            catch (Exception dialogError)
            {
                _logger.LogError(dialogError, @"Could not show the error message");
            }
        }
    }

    private async Task RefreshActivePanel()
    {
        FoldedMenu = _uiConfig.FoldedMenu;
        if (ActivePanel.Panel is IRefreshablePanel refreshable)
            await refreshable.RefreshPanel();
    }

    private async Task RefreshPanels()
    {
        FoldedMenu = _uiConfig.FoldedMenu;
        foreach (var panel in _panels.OfType<IRefreshablePanel>())
            await panel.RefreshPanel();
    }

    private async Task<bool> StartPanelActions()
    {
        foreach (var panel in _panels.OfType<IStartingPanel>())
            if (!await panel.StartPanel())
                return false;
        return true;
    }

    private async Task StartActions()
    {
        try
        {
            if (!await StartPanelActions()) return;
            if (!await _operations.OnBoardingCheckForUpdate()) return;
            if (!await _operations.OnBoardingCheckTrebuchet()) return;
            if (!await _operations.OnBoardingFirstLaunch()) return;
            if (!await _operations.RepairBrokenJunctions()) return;
            await RefreshPanels();
        }
        catch (OperationCanceledException ex)
        {
            await DialogueBox.OpenErrorAndExitAsync(ex.Message);
        }
    }

    public void Dispose()
    {
        _timer.Stop();
    }
}