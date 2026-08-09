using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SteamKit2.GC.CSGO.Internal;
using tot_lib;
using tot_lib.CommandLine;
using TrebuchetLib.Services;

namespace Boulder.Commands;

public class LambClientCommand(AppFiles files, Launcher launcher, ILogger<LambClientCommand> logger) : IInvokableCommand<LambClientCommand>
{
    public static readonly Command Command = CommandBuilder
        .CreateInvokable<LambClientCommand>("client", "Start a conan exile game process and exit")
        .SetServiceConfiguration(Program.ConfigureServices)
        
        .Options.Create<string>("--modlist", "modlist name as seen in trebuchet").AddAlias("-m")
        .SetSetter((c,v) => c.Modlist = v ?? string.Empty).BuildOption()
        
        .Options.Create<string>("--save", "client save name as seen in trebuchet").AddAlias("-s")
        .SetSetter((c,v) => c.Profile = v ?? string.Empty).BuildOption()
        
        .Options.Create<bool>("--battle-eye", "start with battle eye").AddAlias("-b")
        .SetSetter((c,v) => c.BattleEye = v).SetDefault(false).BuildOption()
        
        .Options.Create<string>("--auto-connect", "Try to auto connect to a client connection")
        .SetDefault(string.Empty).AddAlias("-a").SetSetter((c,v) => c.AutoConnect = v ?? string.Empty).BuildOption()
        .BuildCommand();
    public string Profile { get; set; } = string.Empty;
    public string Modlist { get; set; } = string.Empty;
    public bool BattleEye { get; set; } = false;
    public string AutoConnect { get; set; } = string.Empty;
    
    public async Task<int> InvokeAsync(CancellationToken token)
    {
        try
        {
            var data = new Dictionary<string, object>
            {
                { "profile", Profile },
                { "modlist", Modlist },
                { "battle-eye", BattleEye }
            };
            using(logger.BeginScope(data))
                logger.LogInformation("Starting process");
            var process = await launcher.CatapultClientProcess(
                files.Client.Resolve(Profile), 
                files.ResolveModList(Modlist), 
                BattleEye, 
                files.ResolveClientConnectionRef(AutoConnect));
            logger.LogInformation("Process Started: {pid} ({name})", process.Id, process.ProcessName);
            return 0;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Failed to start process");
            return ex.GetErrorCode();
        }
    }
}