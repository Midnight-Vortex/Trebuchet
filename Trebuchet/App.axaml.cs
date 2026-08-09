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
    }

    public void OpenApp(bool testlive, bool enhanced)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            throw new Exception(@"Not supported");
        
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
        ConfigureServices(serviceCollection, testlive, enhanced, catapult, experiment);
        _serviceProvider = serviceCollection.BuildServiceProvider();
        _logger = _serviceProvider.GetRequiredService<ILogger<App>>();
        var osSpecifics = _serviceProvider.GetRequiredService<IOsPlatformSpecific>();
        
        CodeHighlighting.RegisterHighlight(@"Trebuchet.Assets.LogHightlighting.xshd", @"Log", [@".log"]);
        
        _logger.LogInformation(@"Starting Trebuchet");
        _logger.LogInformation(@$"Selecting {(testlive ? @"testlive" : @"live")}");
        if(osSpecifics.IsProcessElevated())
            _logger.LogInformation(@"Process is elevated");

        MainWindow mainWindow = new ();
        var currentWindow = desktop.MainWindow;
        desktop.MainWindow = mainWindow;
        _serviceProvider.GetRequiredService<AppFiles>().SetupFolders();
        mainWindow.DataContext = _serviceProvider.GetRequiredService<TrebuchetApp>();
        mainWindow.Show();
        currentWindow?.Close();
    }
         
    public void Crash() => HasCrashed = true;

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
            
            if (desktop.Args?.Length > 0)
            {
                if (desktop.Args.Contains(Constants.argTestLive))
                {
                    OpenApp(true, false);
                    return;
                }
                else if (desktop.Args.Contains(Constants.argEnhanced))
                {
                    OpenApp(false, true);
                    return;
                }
                else if (desktop.Args.Contains(Constants.argLive))
                {
                    OpenApp(false, false);
                    return;
                }
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
        var version = setup.IsTestLive ? Constants.argTestLive : Constants.argLive;
        List<string> arguments = data.args.Split(' ').ToList();
        if (!arguments.Contains(version))
            arguments.Add(version);
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
    private void ConfigureServices(IServiceCollection services, bool testlive, bool enhanced, bool catapult, bool experiment)
    {
        services.AddSingleton(
            new AppSetup(Config.LoadConfig(Constants.GetConfigPath(testlive, enhanced)), testlive, enhanced, catapult, experiment));
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
        
        Log.Logger = new LoggerConfiguration()
#if !DEBUG
            .MinimumLevel.Information()
#endif
            .WriteTo.Logger(fl => fl
                .WriteTo.File(
                    new ExpressionTemplate("{@t:yyyy-MM-dd HH:mm:ss.fff zzz} " +
                                           "[{@l:u3}]" +
                                           "{#if SourceContext is not null} " +
                                                "{Substring(SourceContext, LastIndexOf(SourceContext, '.') + 1),-15}:" +
                                           "{#end} " +
                                           "{@m} " +
                                           "{#each name, value in Rest(true)}({name}:{value}) {#end}" +
                                           "{#if @x is not null}\n{@x}{#end}\n"),
                    Path.Combine(Constants.GetLoggingDirectory().FullName, @"app.log"),
                    retainedFileTimeLimit: TimeSpan.FromDays(7),
                    rollingInterval: RollingInterval.Day)
                .Filter.ByExcluding(Matching.WithProperty<ConsoleLogSource>(@"TrebSource", _ => true))
            )
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

    private async void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try
        {
            await App.HandleAppCrash(e.Exception);
        }
        catch(Exception ex)
        {
            _logger?.LogCritical(ex, @"OnUnobservedTaskException");
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
        await CrashHandler.Handle(ex, logs);
    }
}