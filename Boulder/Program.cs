using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using Boulder.Commands;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;
using Serilog.Templates;
using Serilog.Templates.Themes;
using tot_lib;
using TrebuchetLib;
using TrebuchetLib.Processes;
using TrebuchetLib.Services;
using TrebuchetLib.YuuIni;
using IConsole = System.CommandLine.IConsole;

namespace Boulder;

class Program
{
    private static bool _testlive = false;
    private static bool _enhanced = false;
    private static bool _experiment = false;
    
    static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("Boulder - Trebuchet's CLI");
        rootCommand.AddCommand(LambCommand.Command);
        rootCommand.AddCommand(KillCommand.Command);
        
        var parser = new CommandLineBuilder(rootCommand).UseDefaults().Build();
        var result = parser.Parse(args);
        _testlive = result.UnmatchedTokens.Contains(Constants.argTestLive);
        _enhanced = result.UnmatchedTokens.Contains(Constants.argEnhanced);
        _experiment = result.UnmatchedTokens.Contains(Constants.argExperiment);
        return await result.InvokeAsync();
    }

    public static void ConfigureServices(IServiceCollection collection)
    {
        collection.AddSingleton(
            new AppSetup(Config.LoadConfig(Constants.GetConfigPath(_testlive, _enhanced)), _testlive, _enhanced, false, _experiment));
        collection.AddLogging(builder => builder.AddSerilog(GetLogger(), true));
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