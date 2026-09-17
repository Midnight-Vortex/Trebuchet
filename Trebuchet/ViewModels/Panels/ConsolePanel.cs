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
using TrebuchetLib.Services;

namespace Trebuchet.ViewModels.Panels;

public class ConsolePanel : ReactiveObject, IRefreshablePanel, ITickingPanel
{
    public ConsolePanel(
        AppSetup setup, 
        Launcher launcher, 
        InternalLogSink logSink, 
        UIConfig uiConfig,
        ILogger<ConsolePanel> logger)
    {
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
        _console = _uiConfig.GetInstancePopup(0) ? _windows.First(x => x.Instance == 0).PopupOut : ConsoleList[0];

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
        var servers = _launcher.GetServerProcesses().ToDictionary(s => s.Infos.Instance);
        AdjustConsoleListIfNeeded(servers.Count == 0 ? 0 : servers.Keys.Max() + 1);
        foreach (var console in ConsoleList)
        {
            console.Process = servers.GetValueOrDefault(console.Instance);
            console.RefreshLabel();
        }
        if (!_userSelectedConsole && SelectedConsole.Process is null)
        {
            var running = ConsoleList.FirstOrDefault(c => c.Process is not null);
            if (running is not null) SelectedConsole = running;
        }

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
            ConsoleList.Add(new MixedConsoleViewModel(_uiConfig, i, _logSink, _logger));
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
        if (_uiConfig.GetInstancePopup(console.Instance))
            Console = _windows.Where(x => x.Instance == console.Instance).Select(x => x.PopupOut).First();
        else
            Console = console;
    }
}
