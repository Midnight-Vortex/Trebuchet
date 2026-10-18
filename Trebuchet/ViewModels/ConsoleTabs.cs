using System;
using System.IO;
using System.Reactive;
using System.Text;
using System.Threading.Tasks;
using ReactiveUI;
using Trebuchet.Assets;

namespace Trebuchet.ViewModels;

public enum ConsoleTabKind
{
    Console,
    Errors,
    Players,
    Chat,
    Mods,
    Steam,
    Trebuchet,
    Ini
}

public abstract class ConsoleTabBase(ConsoleTabKind kind, string name, string icon, string tooltip) : ReactiveObject
{
    private bool _isSelected;

    public ConsoleTabKind Kind { get; } = kind;
    public string Name { get; } = name;
    public string Icon { get; } = icon;
    public string Tooltip { get; } = tooltip;

    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }
}

/// <summary>
/// One filtered, read only log view (DSL tabs: Server Log, Errors, Players, Chat, Mods, SteamCMD, Launcher).
/// </summary>
public sealed class LogTabViewModel : ConsoleTabBase, ITextSource
{
    public LogTabViewModel(ConsoleTabKind kind, string name, string icon, string tooltip, Func<bool> autoScroll)
        : base(kind, name, icon, tooltip)
    {
        _autoScroll = autoScroll;
        Writer = new ConsoleWriter(500, MixedConsoleViewModel.MAX_CHAR);
        Writer.TextFlushed += (_, text) =>
        {
            if (!string.IsNullOrEmpty(text)) TextAppended?.Invoke(this, text);
        };
    }

    private readonly Func<bool> _autoScroll;

    public ConsoleWriter Writer { get; }
    public string Text => Writer.Text;
    public bool AutoScroll => _autoScroll();
    public int MaxChar => MixedConsoleViewModel.MAX_CHAR;

    public event EventHandler<string>? TextAppended;
    public event EventHandler? TextCleared;

    public void Clear()
    {
        Writer.Clear();
        TextCleared?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// Editable server ini file (DSL tabs Engine.ini, ServerSettings.ini, Game.ini).
/// </summary>
public sealed class IniFileTabViewModel : ConsoleTabBase
{
    public IniFileTabViewModel(string iniName, Func<string?> pathResolver)
        : base(ConsoleTabKind.Ini, iniName + @".ini", @"mdi-file-cog-outline", Resources.ConsoleIniTabTip)
    {
        _pathResolver = pathResolver;
        Reload = ReactiveCommand.CreateFromTask(OnReload);
        Save = ReactiveCommand.CreateFromTask(OnSave, this.WhenAnyValue(x => x.IsDirty));
    }

    private readonly Func<string?> _pathResolver;
    private string _text = string.Empty;
    private string _loadedText = string.Empty;
    private string _path = string.Empty;
    private string _status = string.Empty;
    private bool _isDirty;

    public string Text
    {
        get => _text;
        set
        {
            this.RaiseAndSetIfChanged(ref _text, value);
            IsDirty = !string.Equals(_text, _loadedText, StringComparison.Ordinal);
        }
    }

    public string FilePath
    {
        get => _path;
        private set => this.RaiseAndSetIfChanged(ref _path, value);
    }

    public string Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set => this.RaiseAndSetIfChanged(ref _isDirty, value);
    }

    public ReactiveCommand<Unit, Unit> Reload { get; }
    public ReactiveCommand<Unit, Unit> Save { get; }

    public async Task LoadIfClean()
    {
        if (!IsDirty) await OnReload();
    }

    private async Task OnReload()
    {
        var path = _pathResolver();
        FilePath = path ?? string.Empty;
        if (string.IsNullOrEmpty(path))
        {
            SetLoaded(string.Empty);
            Status = Resources.ConsoleIniNoProfile;
            return;
        }

        try
        {
            var content = File.Exists(path) ? await File.ReadAllTextAsync(path) : string.Empty;
            SetLoaded(content);
            Status = File.Exists(path) ? string.Empty : Resources.ConsoleIniNotCreated;
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }

    private async Task OnSave()
    {
        var path = _pathResolver();
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            if (File.Exists(path)) File.Copy(path, path + @".bak", true);
            await File.WriteAllTextAsync(path, Text, new UTF8Encoding(false));
            SetLoaded(Text);
            Status = string.Format(Resources.ConsoleIniSaved, DateTime.Now.ToString(@"HH:mm:ss"));
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }

    private void SetLoaded(string content)
    {
        _loadedText = content;
        Text = content;
    }
}
