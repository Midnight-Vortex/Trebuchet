using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using tot_lib.CommandLine;
using TrebuchetLib;
using TrebuchetLib.DiscordBot;
using TrebuchetLib.Services;

namespace Boulder.Commands;

/// <summary>
/// <c>boulder bot</c>: Trebuchet without the window, see <see cref="BotHost"/>. It has no option on purpose: the token
/// never goes on a command line (other users can read those), it comes from the protected file or the environment.
/// </summary>
public class BotHostCommand(IServiceProvider services, AppSetup setup, ILogger<BotHostCommand> logger) : IInvokableCommand<BotHostCommand>
{
    /// <summary>The named mutex the window takes in Trebuchet/Program.cs: the window or this host, never both.</summary>
    public const string InstallationMutexName = "TotTrebuchet";

    public static readonly Command Command = CommandBuilder
        .CreateInvokable<BotHostCommand>("bot",
            "Run the server automation and the Discord bot without the window, until Ctrl+C. The servers keep running when it ends.")
        .SetServiceConfiguration(Program.ConfigureServices)
        .BuildCommand();

    public async Task<int> InvokeAsync(CancellationToken ct)
    {
        using var signals = new ShutdownSignals(logger);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, signals.Token);
        return await new BotHost(new InstallationParts(services, setup), logger).RunAsync(stop.Token);
    }

    /// <summary>
    /// The services of the bot. Registered for every command but only built when the bot command uses them: types and
    /// factories, nothing is read or started here.
    /// </summary>
    public static void AddServices(IServiceCollection collection)
    {
        // One launcher thread for the tick and for the bot (the IBotDispatcher of LauncherBotActions).
        collection.AddSingleton(services => new SerialBotDispatcher(services.GetRequiredService<ILogger<SerialBotDispatcher>>()));
        collection.AddSingleton<IBotDispatcher>(services => services.GetRequiredService<SerialBotDispatcher>());
        // Strict: a damaged file is never repaired from its backup (that could bring back users or roles removed on purpose).
        collection.AddSingleton(services => DiscordBotConfig.LoadStrict(DiscordBotConfig.GetPath(services.GetRequiredService<AppSetup>().Edition)));
        collection.AddSingleton(services => new BotTokenStore(DiscordBotConfig.GetTokenPath(services.GetRequiredService<AppSetup>().Edition)));
        // In the log folder, next to the logs of the window.
        collection.AddSingleton(_ => new BotAuditLog(Constants.GetLoggingDirectory().FullName));
        collection.AddSingleton<BotRateLimiter>();
        collection.AddSingleton(services => new BotGuard(services.GetRequiredService<BotRateLimiter>()));
        collection.AddSingleton<BotPendingActions>();
        collection.AddSingleton<IBotActions, LauncherBotActions>();
        collection.AddSingleton<IDiscordBotService, DiscordBotService>();
    }

    /// <returns>The lock, or null when the window or another <c>boulder bot</c> holds it (or it can not be taken).</returns>
    public static IDisposable? TryLockInstallation(string name = InstallationMutexName)
    {
        Mutex mutex;
        bool createdNew;
        try
        {
            // The same call as the window. Only the existence of the mutex counts, nobody waits for it.
            mutex = new Mutex(true, name, out createdNew);
        }
        catch (Exception)
        {
            return null;
        }
        if (createdNew) return mutex;
        mutex.Dispose();
        return null;
    }

    /// <returns>The settings, or null when there is no settings file. Nothing is created.</returns>
    /// <exception cref="FormatException">The file is damaged: it is left as it is and not repaired from its backup.</exception>
    public static DiscordBotConfig? LoadSettingsFile(string path) => File.Exists(path) ? DiscordBotConfig.LoadStrict(path) : null;

    public static BotHostTokenCheck CheckToken(BotTokenStore store, Func<string, string?> environment)
    {
        if (store.Load() is not null) return BotHostTokenCheck.Found;
        return string.IsNullOrWhiteSpace(environment(BotTokenStore.EnvironmentVariable))
            ? BotHostTokenCheck.Missing
            : BotHostTokenCheck.VariableNotAToken;
    }

    /// <summary>The installation of the chosen edition, resolved only when the host gets to it.</summary>
    private sealed class InstallationParts(IServiceProvider services, AppSetup setup) : IBotHostParts
    {
        public GameEdition Edition => setup.Edition;

        public IDisposable? TryLockInstallation() => BotHostCommand.TryLockInstallation();

        // The bot uses the same instance, read once.
        public DiscordBotConfig? LoadSettings() =>
            File.Exists(DiscordBotConfig.GetPath(setup.Edition)) ? services.GetRequiredService<DiscordBotConfig>() : null;

        public BotHostTokenCheck CheckToken() => BotHostCommand.CheckToken(services.GetRequiredService<BotTokenStore>(), Environment.GetEnvironmentVariable);

        public SerialBotDispatcher GetDispatcher() => services.GetRequiredService<SerialBotDispatcher>();

        public Task AttachRunningServers() => services.GetRequiredService<Launcher>().AttachRunningServers();

        public Task Tick() => services.GetRequiredService<Launcher>().Tick();

        public IDiscordBotService CreateBot() => services.GetRequiredService<IDiscordBotService>();
    }
}
