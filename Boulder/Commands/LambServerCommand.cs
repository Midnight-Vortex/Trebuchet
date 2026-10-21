using System.CommandLine;
using Microsoft.Extensions.Logging;
using tot_lib;
using tot_lib.CommandLine;
using TrebuchetLib.Services;

namespace Boulder.Commands;

public class LambServerCommand(AppFiles files, Launcher launcher, ILogger<LambServerCommand> logger) : IInvokableCommand<LambServerCommand>
{
    public static readonly Command Command = CommandBuilder
        .CreateInvokable<LambServerCommand>("server", "Start a conan exile server process and exit")
        .SetServiceConfiguration(Program.ConfigureServices)
        .Options.Create<string>("--modlist", "modlist name as seen in trebuchet").AddAlias("-m")
        .SetSetter((c,v) => c.Modlist = v ?? string.Empty).BuildOption()
        .Options.Create<string>("--save", "server save name as seen in trebuchet").AddAlias("-s")
        .SetSetter((c,v) => c.Profile = v ?? string.Empty).BuildOption()
        .Options.Create<int>("--instance", "instance number of your trebuchet install").AddAlias("-i")
        .SetSetter((c,v) => c.Instance = v).BuildOption()
        .BuildCommand();
    
    public string Profile { get; set; } = string.Empty;
    public string Modlist { get; set; } = string.Empty;
    public int Instance { get; set; }
    
    public async Task<int> InvokeAsync(CancellationToken token)
    {
        try
        {
            var data = new Dictionary<string, object>
            {
                { "profile", Profile },
                { "modlist", Modlist },
                { "instance", Instance }
            };
            using(logger.BeginScope(data))
                logger.LogInformation("Starting process");
            // The running servers must be known: their files are not updated and their saves are locked.
            await launcher.AttachRunningServers();
            if (launcher.GetActiveServers().Contains(Instance))
            {
                logger.LogError("Server instance {Instance} is already running", Instance);
                return 1;
            }
            var process = await launcher.CatapultServerProcess(
                files.Server.Resolve(Profile), 
                files.ResolveModList(Modlist), 
                Instance);
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