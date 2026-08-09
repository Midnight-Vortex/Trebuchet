using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using ReactiveUI;
using Trebuchet.Assets;
using Trebuchet.Services;
using TrebuchetLib;
using TrebuchetLib.Services;

namespace Trebuchet.ViewModels.SettingFields;

public class ClientInstallationField : DescriptiveElement<ClientInstallationField>
{
    public ClientInstallationField(Operations operations, AppSetup setup)
    {
        _operations = operations;
        _setup = setup;

        var isInstalled = this.WhenAnyValue(x => x.Installed);
        _installLabel = this.WhenAnyValue(x => x.Installed)
            .Select(x => x ? Resources.Change : Resources.Find)
            .ToProperty(this, x => x.InstallLabel);

        _manageFileLabel = this.WhenAnyValue(x => x.ManageFiles)
            .Select(x => x ? Resources.Yes : Resources.No)
            .ToProperty(this, x => x.ManageFileLabel);
        
        Remove = ReactiveCommand.CreateFromTask(UninstallClientPath, isInstalled);
        
        Install = ReactiveCommand.CreateFromTask(InstallClientPath);

        Installed = Tools.IsClientInstallValid(setup.Config, setup.Edition);
        InstallPath = setup.Config.ClientPath;
        ManageFiles = setup.Config.ManageClient;
    }
    
    private readonly Operations _operations;
    private readonly AppSetup _setup;
    private bool _installed;
    private bool _manageFiles;
    private string _installPath = string.Empty;
    private readonly ObservableAsPropertyHelper<string> _installLabel;
    private readonly ObservableAsPropertyHelper<string> _manageFileLabel;

    public ReactiveCommand<Unit,Unit> Remove { get; }
    public ReactiveCommand<Unit,Unit> Install { get; }

    public bool Installed
    {
        get => _installed;
        set => this.RaiseAndSetIfChanged(ref _installed, value);
    }
    
    public bool ManageFiles
    {
        get => _manageFiles;
        set => this.RaiseAndSetIfChanged(ref _manageFiles, value);
    }

    public string InstallPath
    {
        get => _installPath;
        set => this.RaiseAndSetIfChanged(ref _installPath, value);
    }

    public string InstallLabel => _installLabel.Value;
    public string ManageFileLabel => _manageFileLabel.Value;

    public ClientInstallationField WhenFieldChanged(ReactiveCommand<Unit, Unit> command)
    {
        this.WhenAnyValue(x => x.InstallPath, x => x.ManageFiles)
            .Select((_,_) => Unit.Default)
            .InvokeCommand(command);
        return this;
    }

    private async Task InstallClientPath()
    {
        try
        {
            await _operations.OnBoardingFindConanExile(force: true);
        }
        catch (OperationCanceledException)
        {
            // Path may already be set before a later cancel (e.g. Manage prompt).
        }

        // Always refresh UI from config (cancel/UAC may leave ClientPath set without reaching here previously).
        SyncFromConfig();
        if (Installed)
            _setup.Config.SaveFile();
    }

    public void SyncFromConfig()
    {
        Installed = Tools.IsClientInstallValid(_setup.Config, _setup.Edition);
        InstallPath = _setup.Config.ClientPath ?? "";
        ManageFiles = _setup.Config.ManageClient;
    }

    private async Task UninstallClientPath()
    {
        try
        {
            _setup.Config.ManageClient = false;
            var success = await _operations.OnBoardingApplyConanManagement();
            if (success)
            {
                _setup.Config.ClientPath = string.Empty;
                _setup.Config.SaveFile();;
                InstallPath = string.Empty;
                ManageFiles = false;
                Installed = false;
            }
        }
        catch(OperationCanceledException) {}
    }
}