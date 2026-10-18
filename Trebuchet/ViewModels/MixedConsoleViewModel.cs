using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using DynamicData.Binding;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using Serilog.Events;
using Serilog.Filters;
using Serilog.Formatting.Display;
using Trebuchet.Assets;
using TrebuchetLib;
using TrebuchetLib.Processes;
using tot_gui_lib;

namespace Trebuchet.ViewModels;

public class MixedConsoleViewModel : ReactiveObject, IScrollController
{
    public const int MAX_CHAR = 500000;

    public MixedConsoleViewModel(UIConfig config, int instance, InternalLogSink trebuchetLog, ILogger logger,
        Func<int, string, string?> iniPathResolver)
    {
        trebuchetLog.LogReceived += OnSinkLogReceived;

        Filters.Add(new ConsoleLogFilterViewModel(config, instance, ConsoleLogSource.ServerLog, Resources.ServerLogs, @"mdi-server"));
        Filters.Add(new ConsoleLogFilterViewModel(config, instance, ConsoleLogSource.Trebuchet, Resources.TrebuchetLogs, @"mdi-rocket"));

        _instance = instance;
        _logger = logger;
        _instanceEqual = Matching.WithProperty<int>(@"instance", p => p == _instance);
        _hasAnySource = Matching.WithProperty<ConsoleLogSource>(@"TrebSource", _ => true);
        _isServerLog = Matching.WithProperty<ConsoleLogSource>(@"TrebSource", p => p == ConsoleLogSource.ServerLog);
        _canBeDisplayed = Matching.WithProperty<ConsoleLogSource>(@"TrebSource", p => Filters
            .Where(x => x.IsDisplayed)
            .Select(x => x.LogSource)
            .Append(ConsoleLogSource.RCon)
            .Contains(p));

        Func<bool> autoScroll = () => AutoScroll;
        ConsoleTab = new LogTabViewModel(ConsoleTabKind.Console, Resources.ConsoleTabConsole, @"mdi-console-line", Resources.ConsoleTabConsoleTip, autoScroll);
        _errorsTab = new LogTabViewModel(ConsoleTabKind.Errors, Resources.ConsoleTabErrors, @"mdi-alert-octagon-outline", Resources.ConsoleTabErrorsTip, autoScroll);
        _playersTab = new LogTabViewModel(ConsoleTabKind.Players, Resources.ConsoleTabPlayers, @"mdi-account-group", Resources.ConsoleTabPlayersTip, autoScroll);
        _chatTab = new LogTabViewModel(ConsoleTabKind.Chat, Resources.ConsoleTabChat, @"mdi-chat-outline", Resources.ConsoleTabChatTip, autoScroll);
        _modsTab = new LogTabViewModel(ConsoleTabKind.Mods, Resources.ConsoleTabMods, @"mdi-puzzle-outline", Resources.ConsoleTabModsTip, autoScroll);
        _steamTab = new LogTabViewModel(ConsoleTabKind.Steam, Resources.ConsoleTabSteam, @"mdi-steam", Resources.ConsoleTabSteamTip, autoScroll);
        _trebuchetTab = new LogTabViewModel(ConsoleTabKind.Trebuchet, Resources.ConsoleTabTrebuchet, @"mdi-rocket", Resources.ConsoleTabTrebuchetTip, autoScroll);
        _logTabs = [ConsoleTab, _errorsTab, _playersTab, _chatTab, _modsTab, _steamTab, _trebuchetTab];

        Tabs.AddRange(_logTabs);
        foreach (var ini in new[] { @"Engine", @"ServerSettings", @"Game" })
        {
            var name = ini;
            Tabs.Add(new IniFileTabViewModel(name, () => iniPathResolver(_instance, name)));
        }
        _selectedTab = ConsoleTab;
        ConsoleTab.IsSelected = true;

        var canSendCommand = this.WhenAnyValue(x => x.CanSend, x => x.CommandField,
            (c, f) => c && !string.IsNullOrEmpty(f));

        SendCommand = ReactiveCommand.CreateFromTask(OnSendCommand, canSendCommand);

        ToggleAutoScroll = ReactiveCommand.Create<Unit>((_) => AutoScroll = !AutoScroll);

        ClearText = ReactiveCommand.Create(() =>
        {
            if (SelectedLogTab is { } tab) tab.Clear();
        });

        SelectTab = ReactiveCommand.CreateFromTask<ConsoleTabBase>(OnSelectTab);

        _config = config;
        _historyIndex = config.ConsoleHistory.Count;
        HistoryPrevious = ReactiveCommand.Create(() => BrowseHistory(-1));
        HistoryNext = ReactiveCommand.Create(() => BrowseHistory(1));

        RefreshLabel();
    }

    private Task OnSinkLogReceived(object? sender, IReadOnlyCollection<LogEvent> args)
    {
        // Post, never Invoke: the logging pipeline must not wait on a busy UI thread.
        // The batch may be reused by Serilog once this returns, so take a copy first.
        var events = args.ToList();
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                WriteLogs(events);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, @"Could not display log events");
            }
        });
        return Task.CompletedTask;
    }

    private readonly int _instance;
    private readonly ILogger _logger;
    private IConanServerProcess? _process;
    private string _serverLabel = string.Empty;
    private bool _canSend;
    private bool _autoScroll = true;
    private string _commandField = string.Empty;
    private int _rconMethod;
    private string _onlinePlayers = string.Empty;
    private string _offlineLogFile = string.Empty;
    private readonly UIConfig _config;
    private int _historyIndex;
    private ConsoleTabBase _selectedTab;
    private readonly HashSet<string> _players = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<LogEvent, bool> _instanceEqual;
    private readonly Func<LogEvent, bool> _hasAnySource;
    private readonly Func<LogEvent, bool> _isServerLog;
    private readonly Func<LogEvent, bool> _canBeDisplayed;
    private readonly LogTabViewModel _errorsTab;
    private readonly LogTabViewModel _playersTab;
    private readonly LogTabViewModel _chatTab;
    private readonly LogTabViewModel _modsTab;
    private readonly LogTabViewModel _steamTab;
    private readonly LogTabViewModel _trebuchetTab;
    private readonly LogTabViewModel[] _logTabs;

    private readonly MessageTemplateTextFormatter _textFormater = new (
        @"[{Timestamp:HH:mm:ss}][{Level:u3}] {TrebSource}: {Message:lj}{NewLine}{Exception}");
    private readonly MessageTemplateTextFormatter _serverLineFormater = new (
        @"[{Timestamp:HH:mm:ss}] {Message:lj}{NewLine}");
    private readonly MessageTemplateTextFormatter _trebFormater = new (
        @"[{Timestamp:HH:mm:ss}][{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");

    public event EventHandler? ScrollToEnd;
    public event EventHandler? ScrollToHome;

    public ObservableCollectionExtended<ConsoleLogFilterViewModel> Filters { get; } = [];

    public LogTabViewModel ConsoleTab { get; }

    public ObservableCollectionExtended<ConsoleTabBase> Tabs { get; } = [];

    public ConsoleTabBase SelectedTab
    {
        get => _selectedTab;
        private set
        {
            if (ReferenceEquals(_selectedTab, value)) return;
            _selectedTab.IsSelected = false;
            this.RaiseAndSetIfChanged(ref _selectedTab, value);
            _selectedTab.IsSelected = true;
            this.RaisePropertyChanged(nameof(SelectedLogTab));
            this.RaisePropertyChanged(nameof(SelectedIniTab));
            this.RaisePropertyChanged(nameof(IsConsoleTabSelected));
            this.RaisePropertyChanged(nameof(IsPlayersTabSelected));
        }
    }

    public LogTabViewModel? SelectedLogTab => SelectedTab as LogTabViewModel;
    public IniFileTabViewModel? SelectedIniTab => SelectedTab as IniFileTabViewModel;
    public bool IsConsoleTabSelected => ReferenceEquals(SelectedTab, ConsoleTab);
    public bool IsPlayersTabSelected => ReferenceEquals(SelectedTab, _playersTab);

    public IConanServerProcess? Process
    {
        get => _process;
        set
        {
            if (ReferenceEquals(_process, value)) return;
            var previous = _process;
            this.RaiseAndSetIfChanged(ref _process, value);
            OnProcessChanged((previous, value));
        }
    }

    public bool CanSend
    {
        get => _canSend;
        set => this.RaiseAndSetIfChanged(ref _canSend, value);
    }

    public string ServerLabel
    {
        get => _serverLabel;
        set => this.RaiseAndSetIfChanged(ref _serverLabel, value);
    }

    public bool AutoScroll
    {
        get => _autoScroll;
        set => this.RaiseAndSetIfChanged(ref _autoScroll, value);
    }

    public string CommandField
    {
        get => _commandField;
        set => this.RaiseAndSetIfChanged(ref _commandField, value);
    }

    /// <summary>0 = raw RCon command, otherwise a message sent with broadcast / server (Pippi) / alert.</summary>
    public int RConMethod
    {
        get => _rconMethod;
        set => this.RaiseAndSetIfChanged(ref _rconMethod, value);
    }

    public List<string> RConMethods { get; } =
    [
        Resources.ConsoleMethodCommand,
        @"broadcast",
        @"server (Pippi)",
        @"/alert"
    ];

    /// <summary>Log file followed while the server of this installed instance is off (empty when none).</summary>
    public string OfflineLogFile
    {
        get => _offlineLogFile;
        set => this.RaiseAndSetIfChanged(ref _offlineLogFile, value);
    }

    public string OnlinePlayers
    {
        get => _onlinePlayers;
        private set => this.RaiseAndSetIfChanged(ref _onlinePlayers, value);
    }

    public int Instance => _instance;

    public ReactiveCommand<Unit, Unit> SendCommand { get; }
    public ReactiveCommand<Unit,Unit> ToggleAutoScroll { get; }
    public ReactiveCommand<Unit,Unit> ClearText { get; }
    public ReactiveCommand<ConsoleTabBase, Unit> SelectTab { get; }
    public ReactiveCommand<Unit, Unit> HistoryPrevious { get; }
    public ReactiveCommand<Unit, Unit> HistoryNext { get; }

    public async Task Send(string input)
    {
        try
        {
            if (CanSend && Process?.RCon is { } rcon)
            {
                var command = RConMethod switch
                {
                    1 => @"broadcast " + input,
                    2 => @"server " + input,
                    3 => @"/alert " + input,
                    _ => input
                };
                _logger.LogInformation(@"Send {command}", command);
                await ConsoleTab.Writer.WriteLineAsync(@"> " + command);
                ConsoleTab.Writer.Flush();
                var response = await rcon.Send(command, CancellationToken.None);
                if (response.Exception is not null) throw response.Exception;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, @"Could not Send command");
            await ConsoleTab.Writer.WriteLineAsync($"RCON: {ex.Message}");
            ConsoleTab.Writer.Flush();
        }
    }

    public void WriteLogs(IEnumerable<LogEvent> events)
    {
        var displayTrebuchet = Filters.Any(x => x.LogSource == ConsoleLogSource.Trebuchet && x.IsDisplayed);
        foreach (var log in events)
        {
            try
            {
                Route(log, displayTrebuchet);
            }
            catch (Exception ex)
            {
                // A malformed event must never break the console.
                _logger.LogDebug(ex, @"Could not route log event");
            }
        }
        foreach (var tab in _logTabs)
            tab.Writer.Flush();
        OnScrollToEnd();
    }

    private void Route(LogEvent log, bool displayTrebuchet)
    {
        if (_hasAnySource(log))
        {
            if (!_instanceEqual(log)) return;
            if (_canBeDisplayed(log))
                _textFormater.Format(log, ConsoleTab.Writer);
            if (_isServerLog(log))
                RouteServerLine(log);
            return;
        }

        if (displayTrebuchet)
            _trebFormater.Format(log, ConsoleTab.Writer);
        _trebFormater.Format(log, _trebuchetTab.Writer);
        if (IsSteamEvent(log))
            _trebFormater.Format(log, _steamTab.Writer);
    }

    private void RouteServerLine(LogEvent log)
    {
        var source = GetScalar(log, @"source");
        var content = GetScalar(log, @"content");
        if (string.IsNullOrEmpty(source) && string.IsNullOrEmpty(content))
            content = log.RenderMessage();

        if (log.Level >= LogEventLevel.Error || content.Contains(@"Error:", StringComparison.Ordinal))
            _serverLineFormater.Format(log, _errorsTab.Writer);

        if (source.Contains(@"ChatWindow", StringComparison.OrdinalIgnoreCase)
            || content.Contains(@"ChatWindow", StringComparison.OrdinalIgnoreCase))
            _serverLineFormater.Format(log, _chatTab.Writer);

        if (source.StartsWith(@"LogMod", StringComparison.OrdinalIgnoreCase)
            || content.Contains(@"mod pak", StringComparison.OrdinalIgnoreCase)
            || content.Contains(@"modlist", StringComparison.OrdinalIgnoreCase))
            _serverLineFormater.Format(log, _modsTab.Writer);

        if (TrackPlayers(content))
            _serverLineFormater.Format(log, _playersTab.Writer);
    }

    /// <summary>
    /// Follow "Join succeeded" / "Player disconnected" like the DSL Players tab.
    /// </summary>
    private bool TrackPlayers(string content)
    {
        const string join = @"Join succeeded:";
        const string leave = @"Player disconnected:";
        // The log of a stopped server is history: nobody is connected, only show the lines.
        var live = Process is not null;
        var index = content.IndexOf(join, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            var name = content[(index + join.Length)..].Trim();
            if (live && name.Length > 0 && _players.Add(name)) UpdateOnlinePlayers();
            return true;
        }
        index = content.IndexOf(leave, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            var name = content[(index + leave.Length)..].Trim();
            if (live && _players.Remove(name)) UpdateOnlinePlayers();
            return true;
        }
        return content.Contains(@"Login request", StringComparison.OrdinalIgnoreCase)
               || content.Contains(@"Player Connected", StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateOnlinePlayers()
    {
        OnlinePlayers = _players.Count == 0
            ? string.Empty
            : $@"{_players.Count}: {string.Join(@", ", _players.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))}";
    }

    private static bool IsSteamEvent(LogEvent log)
    {
        var context = GetScalar(log, @"SourceContext");
        if (context.EndsWith(@".Steam", StringComparison.Ordinal) || context.Contains(@"DepotDownloader", StringComparison.Ordinal))
            return true;
        var text = log.MessageTemplate.Text;
        return text.Contains(@"ContentDownloader", StringComparison.Ordinal) || text.Contains(@"Steam", StringComparison.Ordinal);
    }

    private static string GetScalar(LogEvent log, string property)
    {
        return log.Properties.TryGetValue(property, out var value) && value is ScalarValue { Value: { } v }
            ? v.ToString() ?? string.Empty
            : string.Empty;
    }

    private async Task OnSelectTab(ConsoleTabBase tab)
    {
        SelectedTab = tab;
        if (tab is IniFileTabViewModel ini)
            await ini.LoadIfClean();
    }

    private void OnProcessChanged((IConanServerProcess? old, IConanServerProcess? current) args)
    {
        if (args.old is not null)
        {
            args.old.StateChanged -= OnStateChanged;
        }

        if (args.current is not null)
        {
            args.current.StateChanged += OnStateChanged;
        }

        // New server process: the player list starts empty again.
        _players.Clear();
        UpdateOnlinePlayers();
        RefreshLabel();
    }

    private void OnStateChanged(object? sender, ProcessState e)
    {
        if (Dispatcher.UIThread.CheckAccess()) RefreshLabel();
        else Dispatcher.UIThread.Post(RefreshLabel);
    }

    public void RefreshLabel()
    {
        if (Process is not { } process)
        {
            CanSend = false;
            ServerLabel = $@"{Resources.ConsoleNoLocalServer} - {Resources.Instance} {_instance}";
            if (!string.IsNullOrEmpty(OfflineLogFile))
                ServerLabel += $@" - {Resources.ConsoleOfflineLog}";
        }
        else
        {
            CanSend = process.RCon is not null && process.Infos.RConPort > 0
                && process.State is ProcessState.RUNNING or ProcessState.ONLINE;
            var title = string.IsNullOrWhiteSpace(process.Infos.Title) ? Resources.PanelServerConsoles : process.Infos.Title;
            var state = process.State == ProcessState.ONLINE ? Resources.ConsoleOnline
                : process.State == ProcessState.RUNNING ? Resources.ConsoleRunning : process.State.ToString();
            ServerLabel = $@"{title} - {Resources.Instance} {_instance} - {state} - PID {process.PId}";
            if (CanSend) ServerLabel += $" - RCON {IPAddress.Loopback}:{process.Infos.RConPort}";
            else if (process.RCon is null) ServerLabel += $" - {Resources.ConsoleRconDisabled}";
        }
    }

    private void OnScrollToEnd()
    {
        if(AutoScroll)
            ScrollToEnd?.Invoke(this, EventArgs.Empty);
    }

    private void OnScrollToHome()
    {
        if(AutoScroll)
            ScrollToHome?.Invoke(this, EventArgs.Empty);
    }

    private async Task OnSendCommand()
    {
        var command = CommandField;
        CommandField = string.Empty;
        AddToHistory(command);
        await Send(command);
    }

    /// <summary>DSL "Send" box: the last 50 sent texts, browsed with the arrow keys.</summary>
    private void AddToHistory(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return;
        var history = _config.ConsoleHistory;
        history.Remove(command);
        history.Add(command);
        while (history.Count > 50) history.RemoveAt(0);
        _historyIndex = history.Count;
        try { _config.SaveFile(); } catch (Exception ex) { _logger.LogDebug(ex, @"Could not save console history"); }
    }

    private void BrowseHistory(int direction)
    {
        var history = _config.ConsoleHistory;
        if (history.Count == 0) return;
        _historyIndex = Math.Clamp(_historyIndex + direction, 0, history.Count);
        CommandField = _historyIndex >= history.Count ? string.Empty : history[_historyIndex];
    }

    public override string ToString()
    {
        return ServerLabel;
    }
}
