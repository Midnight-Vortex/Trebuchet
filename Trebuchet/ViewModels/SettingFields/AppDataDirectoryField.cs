using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using ReactiveUI;
using Trebuchet.Services;
using TrebuchetLib;
using TrebuchetLib.Services;

namespace Trebuchet.ViewModels.SettingFields;

/// <summary>
/// A directory of the settings that can be changed or reset to its default place; Trebuchet restarts after either.
/// Used for the directory of the downloaded data and for the one of the saves and profiles.
/// </summary>
public class AppDataDirectoryField : DescriptiveElement<AppDataDirectoryField>
{
    /// <summary>The directory of the downloaded data (mods and server files): nothing is moved, it is downloaded again.</summary>
    public AppDataDirectoryField(Operations operations, AppSetup setup) : this(
        setup,
        () => setup.Config.DataDirectory,
        () => AppSetup.GetCommonAppDataDirectoryDefault().FullName,
        operations.OnBoardingChangeDataDirectory,
        () =>
        {
            setup.Config.DataDirectory = string.Empty;
            return Task.FromResult(true);
        })
    {
    }

    /// <param name="getCustom">The directory of the settings, empty for the default place.</param>
    /// <param name="change">Asks for a directory and changes the settings; true when they changed.</param>
    /// <param name="reset">Goes back to the default place; true when the settings changed.</param>
    public AppDataDirectoryField(AppSetup setup, Func<string> getCustom, Func<string> getDefault, Func<Task<bool>> change,
        Func<Task<bool>> reset)
    {
        _setup = setup;
        _getCustom = getCustom;
        _change = change;
        _reset = reset;

        var isCustomized = this.WhenAnyValue(x => x.Customized);
        Remove = ReactiveCommand.CreateFromTask(() => Apply(_reset), isCustomized);
        Change = ReactiveCommand.CreateFromTask(() => Apply(_change));

        _pathDisplay = this.WhenAnyValue(x => x.DataDirectory)
            .Select(x => string.IsNullOrEmpty(x) ? getDefault() : x)
            .ToProperty(this, x => x.PathDisplay);

        Customized = !string.IsNullOrEmpty(getCustom());
        DataDirectory = getCustom();
    }

    /// <summary>The directory of the saves and profiles: they are moved along (<see cref="Operations.OnBoardingChangeProfileDirectory"/>).</summary>
    public static AppDataDirectoryField ForProfiles(Operations operations, AppSetup setup) => new(
        setup,
        () => setup.Config.ProfileDirectory,
        () => AppSetup.GetDataDirectoryDefault().FullName,
        () => operations.OnBoardingChangeProfileDirectory(false),
        () => operations.OnBoardingChangeProfileDirectory(true));

    private readonly AppSetup _setup;
    private readonly Func<string> _getCustom;
    private readonly Func<Task<bool>> _change;
    private readonly Func<Task<bool>> _reset;
    private bool _customized;
    private string _dataDirectory = string.Empty;
    private ObservableAsPropertyHelper<string> _pathDisplay;

    public ReactiveCommand<Unit,Unit> Remove { get; }
    public ReactiveCommand<Unit,Unit> Change { get; }

    public bool Customized
    {
        get => _customized;
        set => this.RaiseAndSetIfChanged(ref _customized, value);
    }

    public string DataDirectory
    {
        get => _dataDirectory;
        set => this.RaiseAndSetIfChanged(ref _dataDirectory, value);
    }

    public string PathDisplay => _pathDisplay.Value;

    public AppDataDirectoryField WhenFieldChanged(ReactiveCommand<Unit, Unit> command)
    {
        this.WhenAnyValue(x => x.DataDirectory)
            .Select((_) => Unit.Default)
            .InvokeCommand(command);
        return this;
    }

    private async Task Apply(Func<Task<bool>> action)
    {
        try
        {
            if (!await action()) return;
            _setup.Config.SaveFile();
            DataDirectory = _getCustom();
            Customized = !string.IsNullOrEmpty(DataDirectory);
            // The panels still hold the old directory: Trebuchet must not keep running when the restart fails.
            try { App.RestartProcess(); }
            catch (Exception) { App.ShutdownDesktopProcess(); }
        }
        catch(OperationCanceledException) {}
    }
}
