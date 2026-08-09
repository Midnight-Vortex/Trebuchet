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

public class ServerInstallationField : DescriptiveElement<ServerInstallationField>
{
    public ServerInstallationField(Operations operations, AppSetup setup)
    {
        _operations = operations;
        _setup = setup;

        var isInstalled = this.WhenAnyValue(x => x.Installed);
        
        Remove = ReactiveCommand.Create(() =>
        {
            setup.Config.ServerInstanceCount = 0;
            setup.Config.SaveFile();
            InstanceCount = 0;
            Installed = false;
        }, isInstalled);
        
        Install = ReactiveCommand.CreateFromTask(InstallClientPath);

        Installed = setup.Config.ServerInstanceCount > 0;
        InstanceCount = setup.Config.ServerInstanceCount;
    }
    
    private readonly Operations _operations;
    private readonly AppSetup _setup;
    private bool _installed;
    private int _instanceCount;

    public ReactiveCommand<Unit,Unit> Remove { get; }
    public ReactiveCommand<Unit,Unit> Install { get; }

    public bool Installed
    {
        get => _installed;
        set => this.RaiseAndSetIfChanged(ref _installed, value);
    }

    public int InstanceCount
    {
        get => _instanceCount;
        set => this.RaiseAndSetIfChanged(ref _instanceCount, value);
    }

    public ServerInstallationField WhenFieldChanged(ReactiveCommand<Unit, Unit> command)
    {
        this.WhenAnyValue(x => x.InstanceCount, x => x.InstanceCount)
            .Select((_,_) => Unit.Default)
            .InvokeCommand(command);
        return this;
    }

    private async Task InstallClientPath()
    {
        try
        {
            var success = await _operations.OnBoardingServerInstanceSelection();
            if (success)
            {
                _setup.Config.SaveFile();
                SyncFromConfig();
            }
        }
        catch(OperationCanceledException) {}
    }

    public void SyncFromConfig()
    {
        InstanceCount = _setup.Config.ServerInstanceCount;
        Installed = _setup.Config.ServerInstanceCount > 0;
    }
}