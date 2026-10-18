using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Configuration;
using Serilog.Filters;
using Serilog.Templates;
using SteamKit2.Internal;
using tot_lib;
using Trebuchet.Services;
using Trebuchet.Services.Language;
using Trebuchet.Utils;
using Trebuchet.ViewModels;
using Trebuchet.ViewModels.InnerContainer;
using Trebuchet.ViewModels.Panels;
using Trebuchet.Windows;
using TrebuchetLib;
using TrebuchetLib.Processes;
using TrebuchetLib.Services;
using TrebuchetLib.Services.Importer;
using TrebuchetLib.YuuIni;
using tot_gui_lib;
using tot_lib.OsSpecific;
using TrebuchetLib.OsSpecific;

// GNU GENERAL PUBLIC LICENSE // Version 2, June 1991
// Copyright (C) 2025 Totchinuko https://github.com/Totchinuko
// Full license text: LICENSE.txt at the project root

namespace Trebuchet;

public partial class App : Application, IApplication
{
    private ILogger<App>? _logger;
    private UIConfig? _uiConfig;
    private LanguageManager? _langManager;
    private InternalLogSink? _internalLogSink;
    private ServiceProvider? _serviceProvider;
    public bool HasCrashed { get; private set; }
    public IImage? AppIconPath => Resources[@"AppIcon"] as IImage;

    public override void Initialize()
    {
        _uiConfig = UIConfig.LoadConfig(AppConstants.GetUIConfigPath());
        var languageConfiguration = new LanguagesConfiguration(AppConstants.UICultureList);
        _langManager = new LanguageManager(languageConfiguration);
        _langManager.SetLanguage(_uiConfig.UICulture);
        
        AvaloniaXamlLoader.Load(this);
#if DEBUG
        this.AttachDeveloperTools();
#endif
    }

    public async Task OpenAppAsync(GameEdition edition)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            throw new Exception(@"Not supported");

        // Per-edition UI config keeps profile selections isolated.
        LoadEditionUiConfig(edition);
        // Initialize() applied the start window's language; the edition settings hold the user's choice.
        if (!string.IsNullOrEmpty(_uiConfig?.UICulture))
            _langManager?.SetLanguage(_uiConfig.UICulture);
        // Same for the theme: the start window applied the one of the Legacy settings.
        Utils.Utils.ApplyPlateformTheme((PlateformTheme)_uiConfig!.PlateformTheme);

        bool catapult = false;
        
        bool experiment = _uiConfig!.Experiments;
        if (desktop.Args?.Length > 0)
        {
            if(desktop.Args.Contains(Constants.argCatapult))
                catapult = true;
            if (desktop.Args.Contains(Constants.argExperiment))
                experiment = true;
        }
   
        
        var serviceCollection = new ServiceCollection();
        ConfigureServices(serviceCollection, edition, catapult, experiment);
        _serviceProvider = serviceCollection.BuildServiceProvider();
        _logger = _serviceProvider.GetRequiredService<ILogger<App>>();
        var osSpecifics = _serviceProvider.GetRequiredService<IOsPlatformSpecific>();
        var setup = _serviceProvider.GetRequiredService<AppSetup>();
        
        CodeHighlighting.RegisterHighlight(@"Trebuchet.Assets.LogHightlighting.xshd", @"Log", [@".log"]);
        CodeHighlighting.RegisterHighlight(@"Trebuchet.Assets.IniHighlighting.xshd", @"Ini", [@".ini"]);
        
        if (_uiConfig!.DebugMode)
        {
            var installLogDir = ResolveDebugLogDirectory();
            _logger.LogInformation("Debug mode ON — debug log: {Path}", Path.Combine(installLogDir, "debug-*.log"));
            _logger.LogInformation("ClientPath: {ClientPath}", setup.Config.ClientPath);
            _logger.LogInformation("Edition: {Edition}", edition);
            _logger.LogInformation("ManageClient: {ManageClient}", setup.Config.ManageClient);
            _logger.LogInformation("IsElevated: {IsElevated}", osSpecifics.IsProcessElevated());
            _logger.LogInformation("OS: {OS}", RuntimeInformation.OSDescription);
            _logger.LogInformation("InstallDirectory: {InstallDirectory}", GetInstallDirectory());
            _logger.LogInformation("BaseDirectory: {BaseDirectory}", AppDomain.CurrentDomain.BaseDirectory);
        }
        
        _logger.LogInformation(@"Starting Trebuchet");
        _logger.LogInformation(@$"Selecting {edition}");
        if(osSpecifics.IsProcessElevated())
            _logger.LogInformation(@"Process is elevated");

        await Task.Run(() => _serviceProvider.GetRequiredService<AppFiles>().SetupFolders());

        MainWindow mainWindow = new ();
        var currentWindow = desktop.MainWindow;
        desktop.MainWindow = mainWindow;
        mainWindow.DataContext = _serviceProvider.GetRequiredService<TrebuchetApp>();
        mainWindow.Show();
        currentWindow?.Close();
    }
         
    private void LoadEditionUiConfig(GameEdition edition)
    {
        var path = AppConstants.GetUIConfigPath(edition);
        if (edition == GameEdition.Legacy || File.Exists(path))
        {
            _uiConfig = UIConfig.LoadConfig(path);
            return;
        }

        // First Enhanced/PTC open: seed shared prefs from Legacy UI, clear edition-specific selections.
        var seed = _uiConfig ?? UIConfig.LoadConfig(AppConstants.GetUIConfigPath(GameEdition.Legacy));
        var created = UIConfig.CreateConfig(path);
        created.UICulture = seed.UICulture;
        created.PlateformTheme = seed.PlateformTheme;
        created.FoldedMenu = seed.FoldedMenu;
        created.DisplayWarningOnKill = seed.DisplayWarningOnKill;
        created.DisplayProcessPerformance = seed.DisplayProcessPerformance;
        created.Experiments = seed.Experiments;
        created.DebugMode = seed.DebugMode;
        created.ConsoleFilters = seed.ConsoleFilters?.ToArray() ?? [];
        created.SaveFile();
        _uiConfig = created;
    }

    public void Crash() => HasCrashed = true;

    /// <summary>
    /// Unhandled error of a ReactiveCommand/observable (e.g. a failing button action): logged and shown as an
    /// error message. ReactiveUI's default would rethrow it and close the whole application.
    /// </summary>
    public static void HandleReactiveError(Exception ex)
    {
        if (Application.Current is not App app) return;
        app._logger?.LogError(ex, @"Unhandled error in a command");
        if (app._serviceProvider?.GetService<DialogueBox>() is not { } box) return;
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await box.OpenErrorAsync(ex.Message);
            }
            catch (Exception boxError)
            {
                app._logger?.LogError(boxError, @"Could not show the error message");
            }
        });
    }

    public static async Task HandleAppCrash(Exception ex)
    {
        if (Application.Current is null) return;
        await ((App)Application.Current).HandleCrash(ex);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            desktop.ShutdownRequested += OnShutdownRequested;

            //CrashHandler.SetReportUri(@"");
            
            Utils.Utils.ApplyPlateformTheme((PlateformTheme)_uiConfig!.PlateformTheme);
            
            if (Constants.ParseEditionArgs(desktop.Args ?? []) is { } edition)
            {
                // Run inside the dispatcher loop: blocking here deadlocks the UI-thread continuation of OpenAppAsync.
                Dispatcher.UIThread.Post(async () => await OpenAppAsync(edition));
                return;
            }
            
            GameBuildViewModel modal = new (this);
            GameBuildWindow window = new ()
            {
                DataContext = modal
            };
            desktop.MainWindow = window;
            window.Show();
        }
        base.OnFrameworkInitializationCompleted();
    }
    
    public static void RestartProcess(bool asAdmin = false)
    {
        if (Application.Current is null) return;
        var provider = ((App)Application.Current)._serviceProvider;
        if (provider is null) return;
        var setup = provider.GetRequiredService<AppSetup>();
        var tOsSpecific = provider.GetRequiredService<ITrebuchetOsSpecific>();
        
        var data = tOsSpecific.GetProcess(Environment.ProcessId);
        List<string> arguments = data.args.Split(' ').ToList();
        foreach (var versionArg in Constants.GetCliArg(setup.Edition).Split(' '))
        {
            if (!arguments.Contains(versionArg))
                arguments.Add(versionArg);
        }
        if(!arguments.Contains(AppConstants.RestartArg))
            arguments.Add(AppConstants.RestartArg);
            
        Process process = new Process();
        process.StartInfo.FileName = data.filename;
        process.StartInfo.Arguments = string.Join(' ', arguments);
        process.StartInfo.UseShellExecute = true;
        if (asAdmin)
            process.StartInfo.Verb = "runas";
        process.Start();
        ShutdownDesktopProcess();
    }
    
    public static void ShutdownDesktopProcess()
    {
        if(Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    [Localizable(false)]
    private void ConfigureServices(IServiceCollection services, GameEdition edition, bool catapult, bool experiment)
    {
        services.AddSingleton(
            new AppSetup(Config.LoadConfig(Constants.GetConfigPath(edition)), edition, catapult, experiment));
        services.AddSingleton(_uiConfig!);
        services.AddSingleton<ILanguageManager>(_langManager!);
        services.AddSingleton<IUpdater>(
            new GithubUpdater(
                AppConstants.GithubOwnerUpdate,
                AppConstants.GithubRepoUpdate,
                AppConstants.GetUpdateContentType()));

        services.AddSingleton(OsPlatformSpecificExtensions.GetOsPlatformSpecific());
        services.AddSingleton(TrebuchetOsSpecificEx.GetOsPlatformSpecific());

        _internalLogSink = new InternalLogSink();
        services.AddSingleton(_internalLogSink);

        string? installLogDir = null;
        if (_uiConfig?.DebugMode == true)
            installLogDir = ResolveDebugLogDirectory();

        var logTemplate = new ExpressionTemplate("{@t:yyyy-MM-dd HH:mm:ss.fff zzz} " +
                                                 "[{@l:u3}]" +
                                                 "{#if SourceContext is not null} " +
                                                      "{Substring(SourceContext, LastIndexOf(SourceContext, '.') + 1),-15}:" +
                                                 "{#end} " +
                                                 "{@m} " +
                                                 "{#each name, value in Rest(true)}({name}:{value}) {#end}" +
                                                 "{#if @x is not null}\n{@x}{#end}\n");

        var loggerConfig = new LoggerConfiguration();
        if (_uiConfig?.DebugMode == true)
            loggerConfig = loggerConfig.MinimumLevel.Debug();
#if !DEBUG
        else
            loggerConfig = loggerConfig.MinimumLevel.Information();
#endif

        loggerConfig = loggerConfig
            .WriteTo.Logger(fl => fl
                .WriteTo.File(
                    logTemplate,
                    Path.Combine(Constants.GetLoggingDirectory().FullName, @"app.log"),
                    retainedFileTimeLimit: TimeSpan.FromDays(7),
                    // 24/7: never stop logging at Serilog's 1 GB default, roll into new files instead.
                    fileSizeLimitBytes: 50L * 1024 * 1024,
                    rollOnFileSizeLimit: true,
                    retainedFileCountLimit: 60,
                    rollingInterval: RollingInterval.Day)
                .Filter.ByExcluding(Matching.WithProperty<ConsoleLogSource>(@"TrebSource", _ => true))
            );

        if (_uiConfig?.DebugMode == true)
        {
            loggerConfig = loggerConfig.WriteTo.Logger(fl => fl
                .WriteTo.File(
                    logTemplate,
                    Path.Combine(installLogDir!, "debug-.log"),
                    retainedFileTimeLimit: TimeSpan.FromDays(14),
                    fileSizeLimitBytes: 100L * 1024 * 1024,
                    rollOnFileSizeLimit: true,
                    retainedFileCountLimit: 100,
                    rollingInterval: RollingInterval.Day)
                .Filter.ByExcluding(Matching.WithProperty<ConsoleLogSource>(@"TrebSource", _ => true))
            );
        }

        Log.Logger = loggerConfig
            .WriteTo.Sink(_internalLogSink, new BatchingOptions()
            {
                BatchSizeLimit = 20,
                BufferingTimeLimit = TimeSpan.FromMilliseconds(500),
                EagerlyEmitFirstEvent = false
            })
            .CreateLogger();

        services.AddLogging(builder => builder.AddSerilog(dispose:true));
        
        services.AddSingleton<AppFiles>();
        services.AddSingleton<BackupManager>();
        services.AddSingleton<ModlistImporter>();
        services.AddSingleton<Operations>();
        services.AddSingleton<IProgressCallback<DepotDownloader.Progress>, Progress>();
        services.AddSingleton<Steam>();
        services.AddSingleton<ConanProcessFactory>();
        services.AddSingleton<Launcher>();
        services.AddSingleton<TaskBlocker>();
        services.AddSingleton<ModFileFactory>();

        services.AddSingleton<SteamWidget>();
        services.AddSingleton<DialogueBox>();
        services.AddSingleton<TrebuchetApp>();
        services.AddTransient<WorkshopSearchViewModel>();
        services.AddTransient<ModListViewModel>();
        services.AddTransient<ClientConnectionListViewModel>();

        services.AddSingleton<IPanel, ModListPanel>();
        services.AddSingleton<IPanel, ClientProfilePanel>();
        services.AddSingleton<IPanel, SyncPanel>();
        services.AddSingleton<IPanel, ServerProfilePanel>();
        services.AddSingleton<IPanel, ConsolePanel>();
       
        services.AddSingleton<IPanel, DashboardPanel>();
        services.AddSingleton<IPanel, ToolboxPanel>();
        services.AddSingleton<IPanel, SettingsPanel>();
    }

    private static string ResolveDebugLogDirectory()
    {
        var preferred = Path.Combine(GetInstallDirectory(), Constants.LogFolder);
        try
        {
            Directory.CreateDirectory(preferred);
            var probe = Path.Combine(preferred, ".write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return preferred;
        }
        catch (Exception)
        {
            var fallback = Constants.GetLoggingDirectory();
            Directory.CreateDirectory(fallback.FullName);
            return fallback.FullName;
        }
    }

    /// <summary>
    /// Directory containing Trebuchet.exe (not a single-file extract temp folder).
    /// </summary>
    private static string GetInstallDirectory()
    {
        var processPath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (!string.IsNullOrEmpty(processPath))
        {
            var dir = Path.GetDirectoryName(processPath);
            if (!string.IsNullOrEmpty(dir))
                return Path.GetFullPath(dir);
        }

        return Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
    }

    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        _internalLogSink?.Dispose();
        _logger?.LogInformation(@"Trebuchet off");
        _logger?.LogInformation(@"----------------------------------------");
        if (_serviceProvider is not null)
            _serviceProvider.Dispose();
    }

    private async void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            e.Handled = true;
            await App.HandleAppCrash(e.Exception);
        }
        catch(Exception ex)
        {
            _logger?.LogCritical(ex, @"OnDispatcherUnhandledException");
        }
    }
    
    private async void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            await App.HandleAppCrash((Exception)e.ExceptionObject);
        }
        catch(Exception ex)
        {
            _logger?.LogCritical(ex, @"OnUnhandledException");
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        // A forgotten background task failing is not fatal in .NET: log it and keep the 24/7 automation running.
        e.SetObserved();
        try
        {
            _logger?.LogError(e.Exception, @"Unobserved task exception");
        }
        catch
        {
            // logging must never throw here
        }
    }
    
    private async Task HandleCrash(Exception ex)
    {
        _logger?.LogError(ex, @"UnhandledException");
        List<CrashHandlerLog> logs = [];
        if (_internalLogSink is not null)
        {
            foreach (var log in _internalLogSink.GetLastLogs())
            {
                logs.Add(new CrashHandlerLog
                {
                    Properties = log.Properties
                        .Select(x => new KeyValuePair<string,string>(x.Key, x.Value.ToString())).ToDictionary(),
                    Date = log.Timestamp.UtcDateTime,
                    LogLevel = Enum.GetName(log.Level) ?? string.Empty,
                    Message = log.RenderMessage()
                });
            }
        }
        // Synchronous on purpose: for a crash on a background thread the process ends at the first await.
        if (TryRestartAfterCrash(ex, logs)) return;
        await CrashHandler.Handle(ex, logs);
    }

    /// <summary>
    /// 24/7 operation: save the crash report and start a new Trebuchet instead of waiting on a modal crash window.
    /// The servers keep running and are attached again. Falls back to the window after 3 crashes in 30 minutes.
    /// </summary>
    private bool TryRestartAfterCrash(Exception ex, List<CrashHandlerLog> logs)
    {
        try
        {
            if (_uiConfig is not { AutoRestartAfterCrash: true } || _serviceProvider is null) return false;
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(processPath)) return false;

            var historyPath = Path.Combine(AppSetup.GetAppConfigDirectory().FullName, @"crash-restarts.json");
            var history = File.Exists(historyPath)
                ? System.Text.Json.JsonSerializer.Deserialize<List<DateTime>>(File.ReadAllText(historyPath)) ?? []
                : [];
            history = history.Where(x => DateTime.UtcNow - x < TimeSpan.FromMinutes(30)).ToList();
            if (history.Count >= 3)
            {
                _logger?.LogCritical(@"Trebuchet crashed {Count} times within 30 minutes, not restarting automatically", history.Count);
                return false;
            }
            history.Add(DateTime.UtcNow);
            File.WriteAllText(historyPath, System.Text.Json.JsonSerializer.Serialize(history));

            var reportPath = Path.Combine(Constants.GetLoggingDirectory().FullName, $@"crash-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.WriteAllText(reportPath, System.Text.Json.JsonSerializer.Serialize(new
            {
                Message = ex.Message,
                Exception = ex.ToString(),
                Version = TrebuchetApp.VersionHeader,
                Logs = logs
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

            var setup = _serviceProvider.GetRequiredService<AppSetup>();
            var arguments = Environment.GetCommandLineArgs().Skip(1).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            foreach (var editionArg in Constants.GetCliArg(setup.Edition).Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (!arguments.Contains(editionArg)) arguments.Add(editionArg);
            if (!arguments.Contains(AppConstants.RestartArg)) arguments.Add(AppConstants.RestartArg);

            var start = new ProcessStartInfo(processPath) { UseShellExecute = false };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            Process.Start(start);
            _logger?.LogCritical(ex, @"Trebuchet crashed and restarted itself, report: {Report}", reportPath);
            Log.CloseAndFlush();

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown(1);
            else
                Environment.Exit(1);
            return true;
        }
        catch (Exception restartError)
        {
            _logger?.LogError(restartError, @"Automatic restart after the crash failed");
            return false;
        }
    }
}
