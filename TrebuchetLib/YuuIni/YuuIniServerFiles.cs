using System.Reflection;
using TrebuchetLib.Processes;
using TrebuchetLib.Services;
using Yuu.Ini;

namespace TrebuchetLib.YuuIni;

public static class YuuIniServerFiles
{
    public static async Task WriteIni(this AppSetup setup, ServerProfile profile, int instance)
    {
        Dictionary<string, IniDocument> documents = new Dictionary<string, IniDocument>();
        // Modify the default SectionName parse because funcom sometime does an oupsi and generate sections with an empty name
        var iniParserConfiguration = new IniParserConfiguration();
        iniParserConfiguration.SectionNameRegex = "\\s*[^\\[\\]]*\\s*";

        foreach (var method in GetIniMethods())
        {
            IniSettingAttribute attr = method.GetCustomAttribute<IniSettingAttribute>() ?? throw new Exception($"{method.Name} does not have IniSettingAttribute.");
            if (!documents.TryGetValue(attr.Path, out IniDocument? document))
            {
                var path = ResolveServerAbsolutePath(setup, profile, instance, attr.Path);
                var content = await Tools.GetFileContent(path);
                document = IniParser.Parse(content, iniParserConfiguration);
                documents.Add(attr.Path, document);
            }
            method.Invoke(null, [setup, profile, document]);
        }

        foreach (var document in documents)
        {
            document.Value.MergeDuplicateSections();
            var path = ResolveServerAbsolutePath(setup, profile, instance, document.Key);
            await Tools.SetFileContent(path, document.Value.ToString()).ConfigureAwait(false);
        }
    }

    public static async Task<ConanServerInfos> GetInfosFromIni(this AppSetup setup, int instance)
    {
        var infos = new ConanServerInfos();
        infos.Instance = instance;

        string initPath = ResolveServerAbsolutePath(setup, instance, string.Format(Constants.FileIniServer, "Engine"));
        IniDocument document = IniParser.Parse(await Tools.GetFileContent(initPath));

        IniSection section = document.GetSection("OnlineSubsystem");
        infos.Title = section.GetValue("ServerName", "Conan Exile Dedicated Server");

        section = document.GetSection("URL");
        infos.Port = section.GetValue("Port", 7777);

        section = document.GetSection("OnlineSubsystemSteam");
        infos.QueryPort = section.GetValue("GameServerQueryPort", 27015);

        document = IniParser.Parse(await Tools.GetFileContent(
            ResolveServerAbsolutePath(setup, instance, string.Format(Constants.FileIniServer, "Game"))));
        section = document.GetSection("RconPlugin");
        infos.RConPassword = section.GetValue("RconPassword", string.Empty);
        infos.RConPort = section.GetValue("RconEnabled", false) ? section.GetValue("RconPort", 25575) : 0;
        return infos;
    }

    /// <summary>
    /// Paths under Instance\...\Saved go through a junction to Documents profiles.
    /// Windows RedirectionGuard blocks traversing those junctions from processes that
    /// enable it — write/read via the resolved profile folder instead (same as client INI).
    /// </summary>
    private static string ResolveServerAbsolutePath(AppSetup setup, ServerProfile profile, int instance, string relativePath)
    {
        var normalized = relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        const string savedPrefix = @"ConanSandbox\Saved\";
        if (normalized.StartsWith(savedPrefix, StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(Path.Combine(profile.ProfileFolder, normalized[savedPrefix.Length..]));

        return Tools.ResolveReparsePointsInPath(Path.Combine(setup.GetInstancePath(instance), relativePath));
    }

    private static string ResolveServerAbsolutePath(AppSetup setup, int instance, string relativePath)
    {
        var instancePath = Path.GetFullPath(setup.GetInstancePath(instance));
        var full = Path.GetFullPath(Path.Combine(instancePath, relativePath));
        var savedRoot = Path.GetFullPath(Path.Combine(instancePath, Constants.FolderGameSave));

        var relativeToSaved = Path.GetRelativePath(savedRoot, full);
        if (relativeToSaved == "."
            || (!relativeToSaved.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relativeToSaved)))
        {
            var physical = Tools.ResolveReparsePointChain(savedRoot);
            var resolved = relativeToSaved == "."
                ? physical
                : Path.Combine(physical, relativeToSaved);
            return Tools.ResolveReparsePointsInPath(resolved);
        }

        return Tools.ResolveReparsePointsInPath(full);
    }
    
    [IniSetting(Constants.FileIniServer, "Engine")]
    public static void ApplyAiSpawn(AppSetup appSetup, ServerProfile profile, IniDocument document)
    {
        document.GetSection("/Script/ConanSandbox.SystemSettings").SetParameter("dw.EnableAISpawning", profile.NoAISpawn ? "0" : "1");
        document.GetSection("/Script/ConanSandbox.SystemSettings").SetParameter("dw.EnableInitialAISpawningPass", profile.NoAISpawn ? "0" : "1");
    }

    [IniSetting(Constants.FileIniServer, "Engine")]
    public static void ApplyEngineSettings(AppSetup appSetup, ServerProfile profile, IniDocument document)
    {
        IniSection section = document.GetSection("OnlineSubsystem");
        section.SetParameter("ServerName", profile.ServerName);
        section.SetParameter("ServerPassword", profile.ServerPassword);

        section = document.GetSection("URL");
        section.SetParameter("Port", profile.GameClientPort.ToString());

        section = document.GetSection("OnlineSubsystemSteam");
        section.SetParameter("GameServerQueryPort", profile.SourceQueryPort.ToString());

        section = document.GetSection("/Script/OnlineSubsystemUtils.IpNetDriver");
        section.SetParameter("NetServerMaxTickRate", profile.MaximumTickRate.ToString());

        section = document.GetSection("Core.Log");
        section.GetParameters().ForEach(section.Remove);

        if (profile.LogFilters.Count > 0)
            foreach (string filter in profile.LogFilters)
            {
                if(!filter.Contains('=')) continue;
                string[] content = filter.Split('=', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if(content.Length < 2) continue;
                section.AddParameter(content[0], content[1]);
            }
        else
            document.Remove(section);
    }

    [IniSetting(Constants.FileIniServer, "Game")]
    public static void ApplyGameSettings(AppSetup appSetup, ServerProfile profile, IniDocument document)
    {
        IniSection section = document.GetSection("/Script/Engine.GameSession");
        section.SetParameter("MaxPlayers", profile.MaxPlayers.ToString());

        section = document.GetSection("RconPlugin");
        section.SetParameter("RconEnabled", profile.EnableRCon.ToString());
        section.SetParameter("RconPort", profile.RConPort.ToString());
        section.SetParameter("RconPassword", profile.RConPassword);
        section.SetParameter("RconMaxKarma", profile.RConMaxKarma.ToString());
    }

    [IniSetting(Constants.FileIniServer, "ServerSettings")]
    public static void ApplyServerSettings(AppSetup appSetup, ServerProfile profile, IniDocument document)
    {
        IniSection section = document.GetSection("ServerSettings");
        section.SetParameter("serverRegion", profile.ServerRegion.ToString());
        section.SetParameter("AdminPassword", profile.AdminPassword);
        section.SetParameter("IsBattlEyeEnabled", profile.EnableBattleEye.ToString());
        section.SetParameter("IsVACEnabled", profile.EnableVAC.ToString());
    }

    [IniSetting(Constants.FileIniDefault, "Engine")]
    public static void ApplySudoSettings(AppSetup appSetup, ServerProfile profile, IniDocument document)
    {
        IniSection section = document.GetSection("/Game/Mods/ModAdmin/Auth/EA_MC_Auth.EA_MC_Auth_C");
        section.GetParameters("+SuperAdminSteamIDs").ForEach(section.Remove);

        if (profile.SudoSuperAdmins.Count != 0)
            foreach (string id in profile.SudoSuperAdmins)
                section.InsertParameter(0, "+SuperAdminSteamIDs", id);
        else
            document.Remove(section);

        section = document.GetSection("/Game/Mods/TotAdmin/Tot_AC_Buildable_RPC.Tot_AC_Buildable_RPC_C");
        section.SetParameter("DisableHighPrecisionMoveTool", profile.DisableHighPrecisionMoveTool ? "True" : "False");
    }
    
    private static IEnumerable<MethodInfo> GetIniMethods()
    {
        return typeof(YuuIniServerFiles).GetMethods()
            .Where(meth => meth.GetCustomAttributes(typeof(IniSettingAttribute), true).Any())
            .Where(meth =>
                meth.GetParameters().Length == 3 && 
                meth.GetParameters()[0].ParameterType == typeof(AppSetup) &&
                meth.GetParameters()[1].ParameterType == typeof(ServerProfile) &&
                meth.GetParameters()[2].ParameterType == typeof(IniDocument)
            );
    }
}
