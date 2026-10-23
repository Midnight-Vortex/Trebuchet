using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Trebuchet.ViewModels.DiscordBot;
using TrebuchetLib.DiscordBot;

namespace Trebuchet.Services;

/// <summary>
/// Owns the Discord bot for the window: creates it on first use, starts it with Trebuchet when that is switched on,
/// and stops it when Trebuchet exits. A bot that can not even be created leaves the rest of Trebuchet working: the
/// panel shows the error instead. Service calls run on the thread pool, so Discord never holds up the UI thread.
/// </summary>
public sealed class DiscordBotHost
{
    private readonly IServiceProvider _services;
    private readonly DiscordBotSettingsFile _settings;
    private readonly IBotDispatcher _dispatcher;
    private readonly ILogger<DiscordBotHost> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _lock = new();
    private IDiscordBotService? _service;
    private bool _created;
    private int _shutDown;

    public DiscordBotHost(IServiceProvider services, DiscordBotSettingsFile settings, IBotDispatcher dispatcher, ILogger<DiscordBotHost> logger)
    {
        _services = services;
        _settings = settings;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    /// <summary>Right after a restart of Trebuchet the old instance may still hold the token for a moment.</summary>
    public TimeSpan TokenInUseRetryDelay { get; set; } = TimeSpan.FromSeconds(10);
    public int TokenInUseAttempts { get; set; } = 3;

    /// <summary>The bot, created on first use. Null when it could not be created or Trebuchet exits.</summary>
    public IDiscordBotService? Service
    {
        get
        {
            EnsureCreated();
            return _service;
        }
    }

    /// <summary>The bot exists already; never creates it.</summary>
    public IDiscordBotService? CreatedService => _service;

    public bool CreationFailed { get; private set; }

    /// <summary>"Start with Trebuchet" is on, but the saved settings were not valid: not started.</summary>
    public bool AutoStartSkipped { get; private set; }

    public bool IsShutDown => Volatile.Read(ref _shutDown) != 0;

    /// <summary>Start, stop and the check are possible (settings readable, bot created, Trebuchet not exiting).</summary>
    public bool IsAvailable => _settings.IsLoaded && !IsShutDown && Service is not null;

    /// <summary>Starts the bot when "start with Trebuchet" is on and the settings are valid. Never throws, never asks.</summary>
    public async Task AutoStartAsync()
    {
        try
        {
            var config = _settings.Config;
            if (!_settings.IsLoaded || !config.AutoStart || IsShutDown) return;
            if (!BotPolicy.Create(config).IsValid)
            {
                AutoStartSkipped = true;
                _logger.LogWarning(@"The Discord bot is not started with Trebuchet: its settings are not valid");
                return;
            }
            var service = Service;
            if (service is null) return;
            _logger.LogInformation(@"Starting the Discord bot with Trebuchet");
            for (var attempt = 1; ; attempt++)
            {
                await Task.Run(() => service.StartAsync(_stopping.Token), _stopping.Token);
                if (service.Problem != DiscordBotProblem.TokenInUse || attempt >= TokenInUseAttempts) break;
                await Task.Delay(TokenInUseRetryDelay, _stopping.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Trebuchet exits.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, @"Starting the Discord bot with Trebuchet failed");
        }
    }

    public Task StartAsync() => RunAsync(service => service.StartAsync(_stopping.Token));

    public Task StopAsync() => RunAsync(service => service.StopAsync());

    public Task ReconnectAsync() => RunAsync(async service =>
    {
        await service.StopAsync();
        await service.StartAsync(_stopping.Token);
    });

    /// <summary>The panel saved the settings: the running bot takes them over without a reconnect.</summary>
    public Task ApplySettingsAsync() => CreatedService is null || IsShutDown
        ? Task.CompletedTask
        : RunAsync(service => service.ApplySettingsAsync());

    public async Task<IReadOnlyList<BotCheckItem>> CheckSecurityAsync(CancellationToken token)
    {
        var service = Available();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _stopping.Token);
        return await Task.Run(() => service.CheckSecurityAsync(linked.Token), linked.Token);
    }

    /// <summary>
    /// Trebuchet exits: queued launcher work is cancelled, the bot disconnects. Waits at most <paramref name="timeout"/>
    /// so a hanging connection can not keep Trebuchet open. Safe to call more than once.
    /// </summary>
    public void Shutdown(TimeSpan timeout)
    {
        if (Interlocked.Exchange(ref _shutDown, 1) != 0) return;
        try
        {
            _stopping.Cancel();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, @"Cancelling the Discord bot failed");
        }
        (_dispatcher as AvaloniaBotDispatcher)?.Close();

        IDiscordBotService? service;
        lock (_lock) service = _service;
        if (service is null) return;
        try
        {
            // On the thread pool: a continuation of the bot that wants the UI thread would wait for this very thread.
            if (!Task.Run(async () => await service.DisposeAsync()).Wait(timeout))
                _logger.LogWarning(@"The Discord bot did not stop within {Timeout}", timeout);
            else
                _logger.LogInformation(@"Discord bot stopped");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, @"Stopping the Discord bot failed");
        }
    }

    private async Task RunAsync(Func<IDiscordBotService, Task> work)
    {
        var service = Available();
        await Task.Run(() => work(service));
    }

    private IDiscordBotService Available()
    {
        if (!_settings.IsLoaded || IsShutDown) throw DiscordBotTexts.ServiceUnavailable();
        return Service ?? throw DiscordBotTexts.ServiceUnavailable();
    }

    private void EnsureCreated()
    {
        lock (_lock)
        {
            if (_created || IsShutDown) return;
            _created = true;
            try
            {
                _service = _services.GetRequiredService<IDiscordBotService>();
            }
            catch (Exception ex)
            {
                CreationFailed = true;
                _logger.LogError(ex, @"The Discord bot could not be created");
            }
        }
    }
}
