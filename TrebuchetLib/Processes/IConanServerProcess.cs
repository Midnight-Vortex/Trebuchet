namespace TrebuchetLib.Processes;

public interface IConanServerProcess : IConanProcess
{
    ConanServerInfos Infos { get; }
    
    public int MaxPlayers { get; }

    public int Players { get; }

    public bool Online { get; }
    
    public bool RequestRestart { get; }
    
    public IRcon? RCon { get; }
    
    public bool KillZombies { get; set; }
    
    public int ZombieCheckSeconds { get; set; }

    /// <summary>
    /// Re-apply priority/affinity when the server changed them (DSL "performance mode" enforcement).
    /// Returns true when something had to be corrected.
    /// </summary>
    bool EnsureProcessSettings(System.Diagnostics.ProcessPriorityClass priority, long affinity);

    Task StopAsync();

    Task RestartAsync();
}