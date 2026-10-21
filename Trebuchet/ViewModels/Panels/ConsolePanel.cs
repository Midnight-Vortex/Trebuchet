using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using DynamicData.Binding;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using Trebuchet.Assets;
using Trebuchet.Windows;
using TrebuchetLib;
using TrebuchetLib.Services;
using TrebuchetLib.YuuIni;

namespace Trebuchet.ViewModels.Panels;

public class ConsolePanel : ReactiveObject, IRefreshablePanel, ITickingPanel
{
    public ConsolePanel(
        AppSetup setup, 
        Launcher launcher, 
        InternalLogSink logSink, 
        UIConfig uiConfig,
        AppFiles appFiles,
        ILogger<LogReader> gameLogger,
        ILogger<ConsolePanel> logger)
    {
        _appFiles = appFiles;
        _gameLogger = gameLogger;
        _setup = setup;
        _launcher = launcher;
        _logSink = logSink;
        _uiConfig = uiConfig;
        _logger = logger;

        AdjustConsoleListIfNeeded();
        for (int i = 0; i < _setup.Config.ServerInstanceCount; i++)
            if(_uiConfig.GetInstancePopup(i))
                CreatePopupWindow(i);

        _selectedConsole = ConsoleList[0];
        _console = (object?)GetPopupWindow(0)?.PopupOut ?? ConsoleList[0];

        CanBeOpened = _setup.Config is { ServerInstanceCount: > 0 };
        PopOut = ReactiveCommand.Create(() =>
        {
            if (Console is MixedConsoleViewModel vm)
            {
                _uiConfig.SetInstancePopup(vm.Instance, true);
                var window = CreatePopupWindow(vm.Instance);
                Console = window.PopupOut;
            }
        });

        OpenPopup = ReactiveCommand.Create(() =>
        {
            PopupOpen = true;
        });
        
        Select = ReactiveCommand.Create<MixedConsoleViewModel>((target) =>
        {
            _userSelectedConsole = true;
            SelectedConsole = target;
            PopupOpen = false;
        });

        this.WhenAnyValue(x => x.SelectedConsole)
            .InvokeCommand(ReactiveCommand.Create<MixedConsoleViewModel>(OnConsoleSelected));

        _isPopupedOut = this.WhenAnyValue(x => x.Console)
            .Select(x => x is MixedConsolePopedOutViewModel)
            .ToProperty(this, x => x.IsPopupedOut);
    }

    private readonly ObservableAsPropertyHelper<bool> _isPopupedOut;
    private readonly List<ConsolePopup> _windows = [];
    private readonly AppSetup _setup;
    private readonly AppFiles _appFiles;
    private readonly ILogger<LogReader> _gameLogger;
    // Server log of installed instances whose server is not running (DSL shows the log even when stopped).
    private readonly Dictionary<int, (LogReader reader, string path)> _offlineReaders = [];
    private readonly HashSet<int> _instancesWithProcess = [];
    private int _ticks;
    private readonly Launcher _launcher;
    private readonly InternalLogSink _logSink;
    private readonly UIConfig _uiConfig;
    private readonly ILogger<ConsolePanel> _logger;
    private object _console;
    private bool _canBeOpened;
    private bool _popupOpen;
    private MixedConsoleViewModel _selectedConsole;
    private bool _userSelectedConsole;

    public string Icon => @"mdi-console-line";
    public string Label => Resources.PanelServerConsoles;

    public bool CanBeOpened
    {
        get => _canBeOpened;
        set => this.RaiseAndSetIfChanged(ref _canBeOpened, value);
    }

    public object Console
    {
        get => _console;
        set => this.RaiseAndSetIfChanged(ref _console, value);
    }

    public bool PopupOpen
    {
        get => _popupOpen;
        set => this.RaiseAndSetIfChanged(ref _popupOpen, value);
    }

    public MixedConsoleViewModel SelectedConsole
    {
        get => _selectedConsole;
        set => this.RaiseAndSetIfChanged(ref _selectedConsole, value);
    }

    public bool IsPopupedOut => _isPopupedOut.Value;

    public ObservableCollectionExtended<MixedConsoleViewModel> ConsoleList { get; } = [];

    public ReactiveCommand<Unit,Unit> OpenPopup { get; }
    public ReactiveCommand<Unit,Unit> PopOut { get; }
    public ReactiveCommand<MixedConsoleViewModel, Unit> Select { get; }

    public Task TickPanel()
    {
        _ticks++;
        var servers = _launcher.GetServerProcesses().ToDictionary(s => s.Infos.Instance);
        AdjustConsoleListIfNeeded(servers.Count == 0 ? 0 : servers.Keys.Max() + 1);
        foreach (var console in ConsoleList)
        {
            console.Process = servers.GetValueOrDefault(console.Instance);
            UpdateOfflineLog(console);
            console.RefreshLabel();
        }
        if (!_userSelectedConsole && SelectedConsole.Process is null)
        {
            var running = ConsoleList.FirstOrDefault(c => c.Process is not null);
            if (running is not null) SelectedConsole = running;
        }

        // Not awaited: a slow RCon answer must not hold up the tick of the whole application.
        foreach (var console in ConsoleList.ToList())
            _ = console.SyncPlayersAsync();
        return Task.CompletedTask;
    }

    public Task RefreshPanel()
    {
        _logger.LogDebug(@"Refresh panel");
        CanBeOpened = _setup.Config is { ServerInstanceCount: > 0 };
        return Task.CompletedTask;
    }

    private void AdjustConsoleListIfNeeded(int discoveredCount = 0)
    {
        int count = Math.Max(Math.Max(1, _setup.Config.ServerInstanceCount), discoveredCount);
        if (ConsoleList.Count >= count) return;
        for (var i = ConsoleList.Count; i < count; i++)
            ConsoleList.Add(new MixedConsoleViewModel(_uiConfig, i, _logSink, _logger, ResolveIniPath));
    }

    /// <summary>
    /// While the server of an installed instance is off, follow the log of its profile so the console and the
    /// log tabs keep working. The running process has its own reader, so the offline one is stopped meanwhile.
    /// </summary>
    private void UpdateOfflineLog(MixedConsoleViewModel console)
    {
        var instance = console.Instance;
        // Give the launcher a few ticks to attach already running servers, so their log is not read twice.
        if (_ticks < 3) return;
        if (console.Process is not null)
        {
            _instancesWithProcess.Add(instance);
            StopOfflineLog(instance);
            console.OfflineLogFile = string.Empty;
            return;
        }

        var path = ResolveGameLogPath(instance);
        if (_offlineReaders.TryGetValue(instance, out var current))
        {
            if (string.Equals(current.path, path, StringComparison.OrdinalIgnoreCase)) return;
            StopOfflineLog(instance);
        }

        console.OfflineLogFile = path ?? string.Empty;
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            var reader = new LogReader(_gameLogger, path).SetContext(@"instance", instance);
            // Right after a server stopped its log is already displayed: only follow new lines.
            if (_instancesWithProcess.Remove(instance)) reader.StartAtEnd();
            else reader.Start();
            _offlineReaders[instance] = (reader, path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, @"Could not follow the server log of instance {Instance}", instance);
        }
    }

    private void StopOfflineLog(int instance)
    {
        if (!_offlineReaders.Remove(instance, out var current)) return;
        try { current.reader.Dispose(); } catch { /* best effort */ }
    }

    private string? ResolveGameLogPath(int instance)
    {
        try
        {
            if (instance >= _setup.Config.ServerInstanceCount) return null;
            if (!_appFiles.Server.TryResolve(_setup.Config.GetInstanceProfile(instance), out var reference)) return null;
            return _appFiles.Server.GetGameLogs(reference);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, @"No game log for instance {Instance}", instance);
            return null;
        }
    }

    private string? ResolveIniPath(int instance, string iniName)
    {
        try
        {
            if (instance >= _setup.Config.ServerInstanceCount) return null;
            if (!_appFiles.Server.TryResolve(_setup.Config.GetInstanceProfile(instance), out var reference)) return null;
            return _setup.GetServerIniPath(reference.Get(), instance, iniName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, @"Could not resolve {Ini} of instance {Instance}", iniName, instance);
            return null;
        }
    }

    private ConsolePopup CreatePopupWindow(int instance)
    {
        if (_windows.Any(x => x.Instance == instance)) return _windows.First(x => x.Instance == instance);
        var window = new ConsolePopup()
        {
            Instance = instance
        };
        window.DataContext = ConsoleList.FirstOrDefault(x => x.Instance == instance);
        window.Show();
        window.Closing += OnWindowClosing;
        _windows.Add(window);
        _uiConfig.SetInstancePopup(instance, true);
        _uiConfig.SaveFile();
        return window;
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (sender is not ConsolePopup window) return;
        if (Console.Equals(window.PopupOut) && window.DataContext is not null)
            Console = window.DataContext;
        _windows.Remove(window);
        _uiConfig.SetInstancePopup(window.Instance, false);
        _uiConfig.SaveFile();
    }

    private void OnConsoleSelected(MixedConsoleViewModel console)
    {
        _logger.LogInformation(@"Opening console {name}", console.ServerLabel);
        Console = (object?)GetPopupWindow(console.Instance)?.PopupOut ?? console;
    }

    /// <summary>
    /// The pop-out window of the instance, or null to show its console docked. The saved pop-out flag can exist
    /// without a window (no server instance configured, instance count reduced, console of a discovered server):
    /// First() threw then, in the constructor this stopped the whole application from opening.
    /// </summary>
    private ConsolePopup? GetPopupWindow(int instance)
    {
        return _uiConfig.GetInstancePopup(instance) ? _windows.FirstOrDefault(x => x.Instance == instance) : null;
    }
}
