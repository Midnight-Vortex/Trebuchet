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
    private ILogger? _processLogger;

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

    public ConanProcessBuilder SetProcessLogger(ILogger logger)
    {
        _processLogger = logger;
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
        
        // The ports come unchecked from the save (the query port field takes any number) or the server ini. An invalid
        // one threw here after the log reader and the query thread were started: every tick leaked both again, and the
        // running server was never attached, so it was started a second time on the same save. Its query and RCon
        // cannot work anyway: the server is watched without them.
        var queryPort = IsValidPort(serverInfo.QueryPort) ? serverInfo.QueryPort : 0;
        var sourceQuery = new SourceQueryReader(new IPEndPoint(IPAddress.Loopback, queryPort), 1500, 2000);

        IRcon? rcon = null;
        if(_useRcon && IsValidPort(serverInfo.RConPort))
            rcon = new Rcon(
                new IPEndPoint(IPAddress.Loopback, serverInfo.RConPort), 
                serverInfo.RConPassword,
                _rconLogger
                ).SetContext("instance", serverInfo.Instance);
        
        var server = new ConanServerProcess(_process, logReader)
        {
            SourceQueryReader = sourceQuery,
            StartUtc = _start,
            RCon = rcon,
            Infos = serverInfo,
            Logger = _processLogger
        };

        // Started last, once nothing can throw any more: they would run on without an owner.
        if(_logAtBeginning) logReader.StartAtBeginning();
        else logReader.Start();
        sourceQuery.StartQueryThread();
        return server;
    }

    private static bool IsValidPort(int port) => port is >= IPEndPoint.MinPort and <= IPEndPoint.MaxPort;

    public Task<IConanProcess> BuildClient()
    {
        if (_process is null) throw new ArgumentNullException(nameof(_process), @"Process is not set");

        return Task.FromResult<IConanProcess>(new ConanClientProcess(_process)
        {
            StartUtc = _start
        });
    }
}