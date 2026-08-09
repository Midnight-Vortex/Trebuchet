using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging;
using TrebuchetLib.Services;

namespace TrebuchetLib.Processes;

public class ConanProcessBuilder : IConanProcessServerBuilderLogTracked
{
    private ConanProcessBuilder()
    {
    }
    
    private DateTime _start = DateTime.UtcNow;
    private FileInfo? _log;
    private Process? _process;
    private bool _logAtBeginning;
    private ILogger<Rcon>? _rconLogger;
    private Func<Task<ConanServerInfos>>? _serverInfosGetter;
    private ILogger<LogReader>? _gameLogger;
    private bool _useRcon;

    public static ConanProcessBuilder Create()
    {
        return new ConanProcessBuilder();
    }

    public IConanProcessBuilder SetStartDate(DateTime time)
    {
        _start = time;
        return this;
    }

    public IConanProcessServerBuilderLogTracked SetLogFile(string logFile)
    {
        _log = new FileInfo(logFile);
        return this;
    }

    public IConanProcessServerBuilderLogTracked StartLogAtBeginning()
    {
        _logAtBeginning = true;
        return this;
    }

    public ConanProcessBuilder SetRConLogger(ILogger<Rcon> logger)
    {
        _rconLogger = logger;
        return this;
    }

    public ConanProcessBuilder SetGameLogger(ILogger<LogReader> logger)
    {
        _gameLogger = logger;
        return this;
    }

    public IConanProcessServerBuilder SetServerInfos(ServerProfile profile, int instance)
    {
        _serverInfosGetter = () => Task.FromResult(new ConanServerInfos(profile, instance));
        return this;
    }

    public IConanProcessServerBuilder SetServerInfos(ConanServerInfos infos)
    {
        _serverInfosGetter = () => Task.FromResult(infos);
        return this;
    }

    public IConanProcessBuilderWithProcess SetProcess(Process process)
    {
        _process = process;
        return this;
    }

    public IConanProcessServerBuilderLogTracked UseRCon()
    {
        _useRcon = true;
        return this;
    }

    public async Task<IConanServerProcess> BuildServer()
    {
        if (_process is null) throw new ArgumentNullException(nameof(_process), @"Process is not set");
        if(_serverInfosGetter is null) throw new ArgumentNullException(nameof(_serverInfosGetter), @"ServerInfos is not set");
        if(_log is null) throw new ArgumentNullException(nameof(_log), @"Log is not set");
        if(_rconLogger is null) throw new ArgumentNullException(nameof(_log), @"Log is not set");
        if(_rconLogger is null) throw new ArgumentNullException(nameof(_rconLogger), @"rcon logger is not set");
        if(_gameLogger is null) throw new ArgumentNullException(nameof(_gameLogger), @"game logger is not set");
        
        var serverInfo = await _serverInfosGetter.Invoke();
        
        var logReader = new LogReader(_gameLogger, _log.FullName)
            .SetContext("instance", serverInfo.Instance);
        
        if(_logAtBeginning) logReader.StartAtBeginning();
        else logReader.Start();
        
        var sourceQuery = new SourceQueryReader(new IPEndPoint(IPAddress.Loopback, serverInfo.QueryPort), 1500, 2000);
        sourceQuery.StartQueryThread();

        IRcon? rcon = null;
        if(_useRcon)
            rcon = new Rcon(
                new IPEndPoint(IPAddress.Loopback, serverInfo.RConPort), 
                serverInfo.RConPassword,
                _rconLogger
                ).SetContext("instance", serverInfo.Instance);
        
        return new ConanServerProcess(_process, logReader)
        {
            SourceQueryReader = sourceQuery,
            StartUtc = _start,
            RCon = rcon,
            Infos = serverInfo
        };
    }

    public Task<IConanProcess> BuildClient()
    {
        if (_process is null) throw new ArgumentNullException(nameof(_process), @"Process is not set");

        return Task.FromResult<IConanProcess>(new ConanClientProcess(_process)
        {
            StartUtc = _start
        });
    }
}