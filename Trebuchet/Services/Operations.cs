using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using tot_lib;
using tot_lib.OsSpecific;
using Trebuchet.Assets;
using Trebuchet.ViewModels;
using Trebuchet.ViewModels.InnerContainer;
using TrebuchetLib;
using TrebuchetLib.OsSpecific;
using TrebuchetLib.Services;

namespace Trebuchet.Services;

public class Operations : IDisposable
{
    public Operations(AppFiles appFiles,
        IOsPlatformSpecific osSpecific,
        ITrebuchetOsSpecific tOsSpecific,
        AppSetup setup, 
        DialogueBox dialogueBox,
        TaskBlocker taskBlocker, 
        ILogger<Operations> logger,
        IUpdater updater,
        Steam steam)
    {
        _appFiles = appFiles;
        _osSpecific = osSpecific;
        _tOsSpecific = tOsSpecific;
        _setup = setup;
        _dialogueBox = dialogueBox;
        _logger = logger;
        _updater = updater;
        _steam = steam;
        
        SetupFileWatcher();
        
        taskBlocker.WhenAnyValue(x => x.CanDownloadMods)
            .InvokeCommand(ReactiveCommand.Create<bool>((x) =>
            {
                _modWatcher.EnableRaisingEvents = x;
            }));
    }
    
    private readonly AppFiles _appFiles;
    private readonly IOsPlatformSpecific _osSpecific;
    private readonly ITrebuchetOsSpecific _tOsSpecific;
    private readonly AppSetup _setup;
    private readonly DialogueBox _dialogueBox;
    private readonly ILogger<Operations> _logger;
    private readonly IUpdater _updater;
    private readonly Steam _steam;
    private FileSystemWatcher _modWatcher;

    public event EventHandler<FileSystemEventArgs>? ModFileChanged; 

    public void Dispose()
    {
        _modWatcher.Dispose();
    }

    public async Task<bool> RepairBrokenJunctions()
    {
        if (!Tools.IsClientInstallValid(_setup.Config, _setup.Edition)) return true;
        return await OnBoardingApplyConanManagement();
    }

    public int CountUnusedMods()
    {
        var installedMods = _steam.GetUGCFileIdsFromStorage();
        var usedMods = _appFiles.Mods.GetList()
            .SelectMany(x => _appFiles.Mods.Get(x).GetWorkshopMods());
        var count = installedMods.Except(usedMods).Count();
        return count;
    }

    public async Task RemoveUnusedMods()
    {
        try
        {
            var installedMods = _steam.GetUGCFileIdsFromStorage();
            var usedMods = _appFiles.Mods.GetList()
                .SelectMany(x => _appFiles.Mods.Get(x).GetWorkshopMods());
            var toRemove = installedMods.Except(usedMods).ToList();
            _steam.ClearUGCFileIdsFromStorage(toRemove);

            _logger.LogInformation(@"Cleaning unused mods");
            foreach (var mod in toRemove)
            {
                if (!_setup.TryGetModPath(mod.ToString(), out var path)) continue;
                if (!File.Exists(path)) continue;
                _logger.LogInformation(path);
                File.Delete(path);
            }
        }
        catch (OperationCanceledException) {}
        catch (Exception ex)
        {
            _logger.LogError(ex, @"Failed to remove unused mods");
            await _dialogueBox.OpenErrorAsync(ex.Message);
        }
    }

    public IDisposable SetDownloaderProgress(IProgress<DepotDownloader.Progress> progress)
    {
        _steam.SetTemporaryProgress(progress);
        return new SteamProgressRestore(_steam);
    }

    public int GetInstalledServerInstanceCount()
    {
        return _steam.GetInstalledInstances();
    }

    public async Task<bool> CheckServerUpdate()
    {
        try
        {
            return await _steam.GetSteamBuildId() != _steam.GetInstanceBuildId(0);
        }
        catch(Exception ex)
        {
            _logger.LogWarning(ex, @"Failed to check server update");
            return false;
        }
    } 
    
    [MemberNotNull("_modWatcher")]
    private void SetupFileWatcher()
    {
        if (_modWatcher != null)
            return;

        _logger.LogInformation(@"Starting mod file watcher");
        var path = Path.Combine(_setup.GetWorkshopFolder());
        if (!Directory.Exists(path))
            Tools.CreateDir(path);

        _modWatcher = new FileSystemWatcher(path);
        _modWatcher.NotifyFilter = NotifyFilters.Attributes
                                   | NotifyFilters.CreationTime
                                   | NotifyFilters.DirectoryName
                                   | NotifyFilters.FileName
                                   | NotifyFilters.LastAccess
                                   | NotifyFilters.LastWrite
                                   | NotifyFilters.Security
                                   | NotifyFilters.Size;
        _modWatcher.Changed += OnModFileChanged;
        _modWatcher.Created += OnModFileChanged;
        _modWatcher.Deleted += OnModFileChanged;
        _modWatcher.Renamed += OnModFileChanged;
        _modWatcher.IncludeSubdirectories = true;
        _modWatcher.EnableRaisingEvents = true;
    }

    private void OnModFileChanged(object sender, FileSystemEventArgs e)
    {
        Dispatcher.UIThread.Invoke(() =>
        {
            ModFileChanged?.Invoke(this, e);
        });
    }
    
    public async Task<bool> OnBoardingFirstLaunch()
    {
        var configPath = Constants.GetConfigPath(_setup.Edition);
        if(File.Exists(configPath)) return true;
        if (!await OnBoardingUsageChoice()) return false;
        _setup.Config.SaveFile();
        return true;
    }
    
    public async Task<bool> OnBoardingCheckForUpdate()
    {
        if (!OperatingSystem.IsWindows()) return true;
        _logger.LogInformation(@"Checking for updates");

        try
        {
            var currentVersion = ProcessUtil.GetAppVersion();
            await _updater.CheckForUpdates(currentVersion);
            if (_updater.IsUpToDate) return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, @"Could not check for update");
            return true; //Pretend that nothing happened
        }

        using(_logger.BeginScope((@"UpdateName", _updater.Name)))
            _logger.LogInformation(@"Update found");
        var title = string.Format(Resources.OnBoardingUpdate, _updater.Version);
        var confirm = new OnBoardingUpdate(title)
            .SetStretch<OnBoardingUpdate>()
            .LoadMarkdownDescription(_updater.Body);
        await _dialogueBox.OpenAsync(confirm);
        if (!confirm.Result) return true;

        try
        {
            _logger.LogInformation(@"Updating");
            var progress = new OnBoardingProgress<long>(title, string.Empty, 0, _updater.Size);
            _dialogueBox.Show(progress);
            var file = await _updater.DownloadUpdate(progress);
            progress.Report(0);

            new Process
            {
                StartInfo = new ProcessStartInfo()
                {
                    FileName = file,
                    // Arguments = "/silent",
                    UseShellExecute = false
                }
            }.Start();
            App.ShutdownDesktopProcess();
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, @"Update failed");
            return true;
        }
    }

    public async Task<bool> OnBoardingChangeDataDirectory()
    {
        var directoryChoice
            = new OnBoardingDirectory(Resources.OnBoardingDataDirectory, Resources.OnBoardingDataDirectorySub)
                .SetValidation(ValidateDataDirectory);
        await _dialogueBox.OpenAsync(directoryChoice);
        if(directoryChoice.Value is null)throw new OperationCanceledException(@"OnBoarding was cancelled");
        using(_logger.BeginScope((@"DataDirectory", directoryChoice.Value)))
            _logger.LogInformation(@"Changing DataDirectory");
        _setup.Config.DataDirectory = directoryChoice.Value;
        return true;
    }

    private Validation ValidateDataDirectory(string? arg)
    {
        if(arg is null) return Validation.Invalid(Resources.ErrorInvalidDirectory);
        if (!AppFiles.IsDirectoryValidForData(arg)) return Validation.Invalid(Resources.ErrorInvalidDirectory);
        return Validation.Valid;
    }

    public async Task<bool> OnBoardingRemoveUnusedMods()
    {
        var count = CountUnusedMods();
        if (count <= 0)
        {
            var message = new OnBoardingMessage(Resources.OnBoardingNoUnusedMods, Resources.OnBoardingNoUnusedModsSub);
            await _dialogueBox.OpenAsync(message);
            return true;
        }

        var confirm = new OnBoardingConfirmation(Resources.OnBoardingModTrimConfirm,
            string.Format(Resources.OnBoardingModTrimConfirmSub, count));
        await _dialogueBox.OpenAsync(confirm);
        if (!confirm.Result) throw new OperationCanceledException(@"OnBoarding was cancelled");
        _logger.LogInformation(@"Cleaning unused mods");
        var progress = new OnBoardingProgress<double>(Resources.OnBoardingModTrimConfirm, string.Empty, 0.0, 1.0);
        progress.Report(0);
        _dialogueBox.Show(progress);
        await RemoveUnusedMods();
        _dialogueBox.Close();
        return true;
    }


    public async Task<bool> OnBoardingUsageChoice()
    {
        var choice = new OnBoardingBranch(Resources.OnBoardingUsageChoice, Resources.OnBoardingUsageChoiceText)
            .AddChoice(Resources.OnBoardingUsageChoicePlayer, Resources.OnBoardingUsageChoicePlayerSub)
            .AddChoice(Resources.OnBoardingUsageChoiceServer, Resources.OnBoardingUsageChoiceServerSub)
            .AddChoice(Resources.OnBoardingUsageChoiceModder, Resources.OnBoardingUsageChoiceModderSub);
        await _dialogueBox.OpenAsync(choice);
        using(_logger.BeginScope((@"UsageChoice", choice.Result)))
            _logger.LogInformation(@"Making usage choice");
        switch (choice.Result)
        {
            case 0: // Play Conan
                _setup.Config.AutoUpdateStatus = AutoUpdateStatus.Never;
                _setup.Config.ServerInstanceCount = 0;
                return await OnBoardingFindConanExile();
            case 1: //Server Admin
                _setup.Config.AutoUpdateStatus = AutoUpdateStatus.CheckForUpdates;
                _setup.Config.ServerInstanceCount = 1;
                return await OnBoardingServerInstanceSelection();
            case 2: // Modding
                _setup.Config.AutoUpdateStatus = AutoUpdateStatus.Never;
                _setup.Config.ServerInstanceCount = 1;
                if(!await OnBoardingFindConanExile()) return false;
                return await OnBoardingServerDownload();
            default:
                throw new OperationCanceledException(@"OnBoarding was cancelled");
        }
    }

    public async Task<bool> OnBoardingServerInstanceSelection()
    {
        if (_steam.Status != SteamStatus.StandBy) return false;
        
        var choice = new OnBoardingIntSlider(
                Resources.OnBoardingServerInstanceCount,
                Resources.OnBoardingServerInstanceCountSub,
                1, 6)
        {
            Value = _setup.Config.ServerInstanceCount
        };
        
        await _dialogueBox.OpenAsync(choice);
        if(choice.Cancelled) throw new OperationCanceledException(@"OnBoarding was cancelled");
        using(_logger.BeginScope((@"ServerInstanceCount", choice.Value)))
            _logger.LogInformation(@"Changing server instance count");
        _setup.Config.ServerInstanceCount = choice.Value;
        return await OnBoardingServerDownload();
    }

    public async Task<bool> OnBoardingServerDownload()
    {
        var progress = new OnBoardingProgress<double>(Resources.UpdateServersLabel, string.Empty, 0.0, 1.0);
        _dialogueBox.Show(progress);
        var progressConverter = new ProgressConverter(progress);
        using var downProgress = SetDownloaderProgress(progressConverter);
        await _steam.UpdateServerInstances();
        progress.Progress = 1.0;
        progress.Close();
        return true;
    }

    public async Task<bool> OnBoardingFindConanExile(bool force = false)
    {
        if (Tools.IsClientInstallValid(_setup.Config.ClientPath, _setup.Edition) && !force)
        {
            await OnBoardingApplyConanManagement();
            return Tools.IsClientInstallValid(_setup.Config, _setup.Edition);
        }
        
        var finder = new OnBoardingDirectory(Resources.OnBoardingLocateConan, Resources.OnBoardingLocateConanText, _setup.Config.ClientPath)
            .SetValidation(ValidateConanExileLocation);
        await _dialogueBox.OpenAsync(finder);
        if(finder.Value is null) throw new OperationCanceledException(@"OnBoarding was cancelled");
        using(_logger.BeginScope((@"ClientPath", finder.Value)))
            _logger.LogInformation(@"Changing conan exiles directory");
        _setup.Config.ClientPath = finder.Value;
        // Persist before management/UAC/copy so Settings keeps the path even if later steps fail.
        _setup.Config.SaveFile();
        await OnBoardingAllowConanManagement();
        return Tools.IsClientInstallValid(_setup.Config, _setup.Edition);
    }

    public Validation ValidateConanExileLocation(string? path)
    {
        if(string.IsNullOrEmpty(path))
            return Validation.Invalid(Resources.ErrorValueEmpty);
        if (!Tools.IsClientInstallValid(path, _setup.Edition))
            return new Validation(false, string.Format(
                Resources.ErrorClientWrongEdition,
                Constants.GetEditionDisplayName(_setup.Edition),
                Constants.GetClientBin(_setup.Edition)));
        return new Validation(true, string.Empty);
    }

    public async Task<bool> OnBoardingAllowConanManagement()
    {
        var choice = new OnBoardingBranch(Resources.OnBoardingManageConan, Resources.OnBoardingManageConanText)
            .AddChoice(Resources.OnBoardingManageConanNo, Resources.OnBoardingManageConanNoSub)
            .AddChoice(Resources.OnBoardingManageConanYes, Resources.OnBoardingManageConanYesSub);
        await _dialogueBox.OpenAsync(choice);
        if(choice.Result < 0) throw new OperationCanceledException(@"OnBoarding was cancelled");
        using(_logger.BeginScope((@"ManageClient", choice.Result)))
            _logger.LogInformation(@"Change client management");
        _setup.Config.ManageClient = choice.Result == 1;
        _setup.Config.SaveFile();
        return await OnBoardingApplyConanManagement();
    }

    public async Task<bool> OnBoardingApplyConanManagement()
    {
        if (!Tools.IsClientInstallValid(_setup.Config, _setup.Edition)) return false;
        var clientDirectory = Path.GetFullPath(_setup.Config.ClientPath);
        var savedDir = Path.Combine(clientDirectory, Constants.FolderGameSave);
        using(_logger.BeginScope((@"SavedDir", savedDir)))
            _logger.LogInformation(@"Applying Conan Management");

        if (!Directory.Exists(savedDir))
        {
            _logger.LogWarning(@"Saved Directory does not exists");
            if (!await OnBoardingElevationRequest(clientDirectory, Resources.OnBoardingManageConanUac)) return false;
            if (_setup.Config.ManageClient)
            {
                _logger.LogInformation(@"Creating junction");
                _osSpecific.MakeSymbolicLink(savedDir, _setup.GetPrimaryJunction());
            }
            else
            {
                if (!_appFiles.Client.GetList().Any())
                    return true;
                var saveName = await OnBoardingChooseClientSave();
                _logger.LogInformation(@"Copying trebuchet save back to game {saveName}", saveName);
                Directory.CreateDirectory(savedDir);
                await Tools.DeepCopyAsync(_appFiles.Client.GetDirectory(_appFiles.Client.Ref(saveName)), savedDir, CancellationToken.None);
            }
            return true;
        }

        if (_osSpecific.IsSymbolicLink(savedDir) && !_setup.Config.ManageClient)
        {
            _logger.LogWarning(@"Junction found, but does not manage");
            if (!_appFiles.Client.GetList().Any())
                return true;
            if (!await OnBoardingElevationRequest(clientDirectory, Resources.OnBoardingManageConanUac)) return false;
            var saveName = await OnBoardingChooseClientSave();
            _logger.LogInformation(@"Copying trebuchet save back to game {saveName}", saveName);
            _osSpecific.RemoveSymbolicLink(savedDir);
            Directory.CreateDirectory(savedDir);
            await Tools.DeepCopyAsync(_appFiles.Client.GetDirectory(_appFiles.Client.Ref(saveName)), savedDir, CancellationToken.None);
            return true;
        }

        if (!_osSpecific.IsSymbolicLink(savedDir) && _setup.Config.ManageClient)
        {
            _logger.LogWarning(@"Directory found, but manage game");
            if (!await OnBoardingElevationRequest(clientDirectory, Resources.OnBoardingManageConanUac)) return false;
            var saveName = await OnBoardingChooseClientSaveName();
            _logger.LogInformation(@"Copying game save into trebuchet {saveName}", saveName);
            await Tools.DeepCopyAsync(savedDir, _appFiles.Client.GetDirectory(_appFiles.Client.Ref(saveName)), CancellationToken.None);
            await OnBoardingSafeIO(() => Directory.Delete(savedDir, true),savedDir);
            _osSpecific.MakeSymbolicLink(savedDir, _setup.GetPrimaryJunction());
            return true;
        }

        if (_osSpecific.IsSymbolicLink(savedDir))
        {
            var path = _osSpecific.GetSymbolicLinkTarget(savedDir);
            if (Path.GetFullPath(path) != Path.GetFullPath(_setup.GetPrimaryJunction()))
            {
                if (!await OnBoardingElevationRequest(clientDirectory, Resources.OnBoardingManageConanUac)) return false;
                _logger.LogWarning(@"Broken junction found, repairing");
                _osSpecific.MakeSymbolicLink(savedDir, _setup.GetPrimaryJunction());
            }
        }

        return true;
    }

    public async Task<string> OnBoardingChooseClientSave()
    {
        var list = _appFiles.Client.GetList().ToList();
        if (list.Count == 0)
            return await OnBoardingChooseClientSaveName();
        var choice = new OnBoardingListSelection(
                Resources.OnBoardingGameSave, 
                Resources.OnBoardingChooseGameSaveText, 
                _appFiles.Client.GetList().Select(x => x.Name).ToList());
        await _dialogueBox.OpenAsync(choice);
        if(string.IsNullOrEmpty(choice.Value)) throw new OperationCanceledException(@"OnBoarding was cancelled");
        return choice.Value;
    }

    public async Task<string> OnBoardingChooseClientSaveName()
    {
        var choice = new OnBoardingNameSelection( Resources.OnBoardingGameSave, Resources.OnBoardingNewGameSave)
            .SetValidation(ValidateClientSaveName);
        await _dialogueBox.OpenAsync(choice);
        if(string.IsNullOrEmpty(choice.Value)) throw new OperationCanceledException(@"OnBoarding was cancelled");
        return choice.Value;
    }

    public Validation ValidateClientSaveName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return new Validation(false, Resources.ErrorNameEmpty);
        if(_appFiles.Client.GetList().Any(x => x.Name == name))
            return new Validation(false, Resources.ErrorNameAlreadyTaken);
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return new Validation(false, Resources.ErrorNameInvalidCharacters);
        return new Validation(true, string.Empty);
    }
    
    public async Task<bool> OnBoardingElevationRequest(string path, string reason)
    {
        var canWriteInTrebuchet = Tools.IsDirectoryWritable(path);
        var isRoot = _osSpecific.IsProcessElevated();
        if (!canWriteInTrebuchet)
        {
            if(isRoot) 
                throw new IOException(@$"Can't write in {path}, permission denied");
            var uac = new OnBoardingBranch(Resources.UACDialog, Resources.UACDialogText + Environment.NewLine + reason)
                .AddChoice(Resources.UACDialog, Resources.OnBoardingUpgradeSub);
            
            var scopeData = new Dictionary<string, object>
            {
                { @"path", path },
                { @"reason", reason }
            };
            using(_logger.BeginScope(scopeData))
                _logger.LogInformation(@"Requesting elevation");
            await _dialogueBox.OpenAsync(uac);
            if(uac.Result < 0) throw new OperationCanceledException(@"OnBoarding was cancelled");

            // Persist ClientPath / ManageClient before elevation restart so Settings shows them after relaunch.
            _setup.Config.SaveFile();
            _logger.LogInformation(@"Restarting as elevated");
            App.RestartProcess(true);
            return false;
        }

        return true;
    }

    public async Task<bool> OnBoardingSafeIO(Action ioAction, string file)
    {
        var currentPopup = _dialogueBox.Popup;
        _dialogueBox.Close();
        while (true)
        {
            try
            {
                ioAction.Invoke();
                break;
            }
            catch (IOException ex)
            {
                if (tot_lib.Utils.IsFileLocked(ex))
                {
                    if (!_dialogueBox.Active)
                    {
                        var message = new OnBoardingProgress<double>(
                            Resources.OnBoardingProcessLock,
                            string.Format(Resources.OnBoardingProcessLockSub, file),
                            0.0, 1.0);
                        message.Report(0);
                        _dialogueBox.Show(message);
                    }
                }
                else
                    throw;
            }
            await Task.Delay(1000);
        }
        if(_dialogueBox.Active)
            _dialogueBox.Close();
        if(currentPopup is not null)
            _dialogueBox.Show(currentPopup);
        return true;
    }
    
    #region Onboarding Upgrade

#pragma warning disable CS0612 // Type or member is obsolete
    public async Task<bool> OnBoardingCheckTrebuchet()
    {
        // Check old versions of trebuchet for upgrade path
        string configLive = @"Live.Config.json";
        string configTestlive = @"TestLive.Config.json";
        var trebuchetDir = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
        configLive = Path.Combine(trebuchetDir, configLive);
        configTestlive = Path.Combine(trebuchetDir, configTestlive);
        _logger.LogInformation(@"Upgrading old trebuchet");
        
        if (File.Exists(configLive) || File.Exists(configTestlive))
        {
            var upgrade = new OnBoardingBranch(Resources.Upgrade, Resources.OnBoardingUpgrade)
                .AddChoice(Resources.Upgrade, Resources.OnBoardingUpgradeSub);
            await _dialogueBox.OpenAsync(upgrade);
            if(upgrade.Result < 0) throw new OperationCanceledException(@"OnBoarding was cancelled");

            if (!await OnBoardingElevationRequest(trebuchetDir, Resources.OnBoardingUpgradeUac)) return false;

            var progress = new OnBoardingProgress<double>(Resources.Upgrade, Resources.OnBoardingUpgradeCopy, 0.0, 1.0);
            _dialogueBox.Show(progress);

            if (File.Exists(configLive))
            {
                _logger.LogInformation(@"Upgrading live");
                var configJson = await File.ReadAllTextAsync(configLive);
                var configuration = JsonSerializer.Deserialize<Config>(configJson);
                if (configuration is not null)
                {
                    if (!await OnBoardingUpgradeTrebuchet(configuration.InstallPath, false, progress)) return false;
                    configuration.InstallPath = string.Empty;
                    configuration.ManageClient = true;
                    configJson = JsonSerializer.Serialize(configuration);
                    await File.WriteAllTextAsync(Constants.GetConfigPath(false), configJson);
                }
                File.Delete(configLive);
            }

            if (File.Exists(configTestlive))
            {
                _logger.LogInformation(@"Upgrading testlive");
                var configJson = await File.ReadAllTextAsync(configTestlive);
                var configuration = JsonSerializer.Deserialize<Config>(configJson);
                if (configuration is not null)
                {
                    if(!await OnBoardingUpgradeTrebuchet(configuration.InstallPath, true, progress)) return false;
                    configuration.InstallPath = string.Empty;
                    configuration.ManageClient = true;
                    configJson = JsonSerializer.Serialize(configuration);
                    await File.WriteAllTextAsync(Constants.GetConfigPath(true), configJson);
                }
                File.Delete(configTestlive);
            }

            App.RestartProcess();
            return false;
        }
        
        return true;
    }
#pragma warning restore CS0612 // Type or member is obsolete

    public async Task<bool> OnBoardingUpgradeTrebuchet(string installDir, bool testlive, IProgress<double> progress)
    {
        bool isElevated = _osSpecific.IsProcessElevated();
        string appDir = Path.GetDirectoryName(AppDomain.CurrentDomain.BaseDirectory) ?? throw new Exception(@"App is installed in an invalid directory");
        installDir = installDir.Replace(@"%APP_DIRECTORY%", appDir); 
        if(string.IsNullOrEmpty(installDir)) return true;
        if(!Directory.Exists(installDir)) return true;
        if (!await OnBoardingElevationRequest(installDir, Resources.OnBoardingUpgradeUac)) return false;
        
        progress.Report(0.0);
        var versionDir = testlive ? Constants.FolderTestLive : Constants.FolderLive;
        var workshopDir = Path.Combine(installDir, Constants.FolderWorkshop);
        if (Directory.Exists(workshopDir))
        {
            _logger.LogInformation(@"Copying workshop directory {directory}", workshopDir);
            await Tools.DeepCopyAsync(workshopDir, _setup.GetWorkshopFolder(), CancellationToken.None, progress);
            if (isElevated)
                _tOsSpecific.SetEveryoneAccess(new DirectoryInfo(_setup.GetWorkshopFolder()));
            await OnBoardingSafeIO(() => Directory.Delete(workshopDir, true), workshopDir);
        }
        
        progress.Report(0.0);
        var instanceDir = Path.Combine(installDir, versionDir, Constants.FolderServerInstances);
        if (Directory.Exists(instanceDir))
        {
            _logger.LogInformation(@"Copying instance directory {directory}", instanceDir);
            Tools.RemoveAllJunctions(instanceDir); 
            await Tools.DeepCopyAsync(instanceDir, _setup.GetBaseInstancePath(testlive), CancellationToken.None, progress);
            if(isElevated)
                _tOsSpecific.SetEveryoneAccess(new DirectoryInfo(_setup.GetBaseInstancePath(testlive)));
            await OnBoardingSafeIO(() => Directory.Delete(instanceDir, true), instanceDir);
        }
        progress.Report(0.0);
        
        var dataDir = Path.Combine(installDir, versionDir);
        if (Directory.Exists(dataDir))
        {
            _logger.LogInformation(@"Copying data directory {directory}", dataDir);
            await Tools.DeepCopyAsync(dataDir, Path.Combine(_setup.GetDataDirectory().FullName, versionDir), CancellationToken.None, progress);
            if(isElevated)
                _tOsSpecific.SetEveryoneAccess(new DirectoryInfo(Path.Combine(_setup.GetDataDirectory().FullName, versionDir)));
            await OnBoardingSafeIO(() => Directory.Delete(dataDir, true),dataDir);
        }
        
        progress.Report(0.0);
        var logsDir = Path.Combine(installDir, @"Logs");
        if (Directory.Exists(logsDir))
        {
            _logger.LogInformation(@"Deleting old logs {directory}", logsDir);
            await OnBoardingSafeIO(() => Directory.Delete(logsDir, true), logsDir);
        }
        
        progress.Report(1.0);
        return true;
    }
    
    #endregion
}