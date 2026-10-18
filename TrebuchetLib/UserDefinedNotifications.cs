using TrebuchetLib.Services;

namespace TrebuchetLib;

public static class UserDefinedNotifications
{
    public static string GetCrashNotification(this AppSetup setup, string serverName)
    {
        var template = setup.Config.NotificationServerCrash;
        return template.Replace("{serverName}", serverName);
    }

    public static string GetOnlineNotification(this AppSetup setup, string serverName)
    {
        var template = setup.Config.NotificationServerOnline;
        return template.Replace("{serverName}", serverName);
    }

    public static string GetReasonModUpdate(this AppSetup setup, IEnumerable<PublishedMod> mods)
    {
        var modNames = string.Join(", ", mods.Select(x => x.Title));
        var template = setup.Config.NotificationServerModUpdate;
        return template.Replace("{modList}", modNames);
    }
    
    public static string GetReasonServerUpdate(this AppSetup setup)
    {
        return setup.Config.NotificationServerServerUpdate;
    }
    
    public static string GetReasonManualShutdown(this AppSetup setup)
    {
        return setup.Config.NotificationServerManualStop;
    }
    
    public static string GetReasonManualRestart(this AppSetup setup)
    {
        return setup.Config.NotificationServerManualRestart;
    }

    public static string GetReasonAutomatedRestart(this AppSetup setup)
    {
        return setup.Config.NotificationServerAutomatedRestart;
    }

    public static string GetServerShutdownNotification(this AppSetup setup, string reason)
    {
        var template = setup.Config.NotificationServerStop;
        return template.Replace("{Reason}", reason);
    }
}