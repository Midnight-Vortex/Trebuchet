using System.CommandLine;
using Boulder.Commands;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;
using Serilog.Templates;
using Serilog.Templates.Themes;
using tot_lib;
using tot_lib.OsSpecific;
using TrebuchetLib;
using TrebuchetLib.OsSpecific;
using TrebuchetLib.Processes;
using TrebuchetLib.Services;
using TrebuchetLib.YuuIni;

namespace Boulder;

class Program
{
    private static GameEdition _edition = GameEdition.Legacy;
    private static bool _experiment = false;
    // tot-lib builds the service provider of a command but never disposes it. Singleton factories receive the root
    // provider, so it is captured in ConfigureServices and disposed at the end of Main: that flushes and closes the
    // Serilog logger and disconnects the Steam session cleanly.
    private static IServiceProvider? _services;

    static async Task<int> Main(string[] args)
    {
        var rootCommand = RootCommandFactory.Create();

        var result = rootCommand.Parse(args);
        _edition = Constants.ParseEditionArgs(args) ?? GameEdition.Legacy;
        _experiment = args.Contains(Constants.argExperiment);
        try
        {
            return await result.InvokeAsync();
        }
        finally
        {
            if (_services is IAsyncDisposable services)
                await services.DisposeAsync();
        }
    }

    public static void ConfigureServices(IServiceCollection collection)
    {
        // Mirrors Trebuchet's App.ConfigureServices for the services the commands need (Launcher depends on the OS
        // services and on Steam, Steam on the progress callback); without them lamb and kill failed with
        // "Unable to resolve service" before doing anything.
        var appSetup = new AppSetup(Config.LoadConfig(Constants.GetConfigPath(_edition)), _edition, false, _experiment);
        collection.AddSingleton(services =>
        {
            _services = services;
            return appSetup;
        });
        collection.AddLogging(builder => builder.AddSerilog(GetLogger(), true));
        collection.AddSingleton(OsPlatformSpecificExtensions.GetOsPlatformSpecific());
        collection.AddSingleton(TrebuchetOsSpecificEx.GetOsPlatformSpecific());
        collection.AddSingleton<IProgressCallback<DepotDownloader.Progress>, TrebuchetLib.Services.Progress>();
        collection.AddSingleton<Steam>();
        collection.AddSingleton<BackupManager>();
        collection.AddSingleton<ConanProcessFactory>();
        collection.AddSingleton<AppFiles>();
        collection.AddSingleton<Launcher>();
    }

    static ILogger GetLogger()
    {
        return new LoggerConfiguration()
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
                    Path.Combine(Constants.GetLoggingDirectory().FullName, @"boulder.log"),
                    retainedFileTimeLimit: TimeSpan.FromDays(7),
                    rollingInterval: RollingInterval.Day)
            )
            .WriteTo.Console(
                new ExpressionTemplate("[{@t:HH:mm:ss} {@l:u3}] " +
                                       "{#if TrebSource is not null}" +
                                            "{TrebSource}:" +
                                       "{#end}" +
                                       "{@m}\n{@x}",
                theme: TemplateTheme.Code
                ))
            .CreateLogger();
    }
}
