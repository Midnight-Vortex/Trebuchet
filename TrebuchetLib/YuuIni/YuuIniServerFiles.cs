using System.Globalization;
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
        var iniParserConfiguration = CreateParserConfiguration();

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

    /// <summary>
    /// Physical path of a server ini (Engine, Game, ServerSettings) of a profile.
    /// </summary>
    public static string GetServerIniPath(this AppSetup setup, ServerProfile profile, int instance, string iniName)
    {
        return ResolveServerAbsolutePath(setup, profile, instance, string.Format(Constants.FileIniServer, iniName));
    }

    public static async Task<ConanServerInfos> GetInfosFromIni(this AppSetup setup, int instance)
    {
        var infos = new ConanServerInfos();
        infos.Instance = instance;

        // Same rules as WriteIni: the default parser threw on the empty section names Funcom writes, so a running server
        // was never attached again after a restart of Trebuchet (and a second one was started on its instance).
        var configuration = CreateParserConfiguration();
        string initPath = ResolveServerAbsolutePath(setup, instance, string.Format(Constants.FileIniServer, "Engine"));
        IniDocument document = IniParser.Parse(await Tools.GetFileContent(initPath), configuration);

        IniSection section = document.GetSection("OnlineSubsystem");
        infos.Title = section.GetValue("ServerName", "Conan Exile Dedicated Server");

        section = document.GetSection("URL");
        infos.Port = section.GetValue("Port", 7777);

        section = document.GetSection("OnlineSubsystemSteam");
        infos.QueryPort = section.GetValue("GameServerQueryPort", 27015);

        document = IniParser.Parse(await Tools.GetFileContent(
            ResolveServerAbsolutePath(setup, instance, string.Format(Constants.FileIniServer, "Game"))), configuration);
        section = document.GetSection("RconPlugin");
        infos.RConPassword = section.GetValue("RconPassword", string.Empty);
        infos.RConPort = section.GetValue("RconEnabled", false) ? section.GetValue("RconPort", 25575) : 0;
        return infos;
    }

    // Modify the default SectionName parse because funcom sometime does an oupsi and generate sections with an empty name
    private static IniParserConfiguration CreateParserConfiguration()
    {
        var configuration = new IniParserConfiguration();
        configuration.SectionNameRegex = "\\s*[^\\[\\]]*\\s*";
        return configuration;
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
        // DSL 1.9.1: a leftover build id override makes the server invisible to up-to-date clients.
        section.GetParameters("bUseBuildIdOverride").ForEach(section.Remove);
        section.GetParameters("BuildIdOverride").ForEach(section.Remove);

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
                section.AddParameter(content[0], LogFilterRules.NormalizeVerbosity(content[1]));
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
        // Same key as the "Voice Chat" toggle of the in-game admin panel; untouched unless forced.
        if (profile.VoiceChatMode == ServerProfile.VoiceChatDisabled)
            section.SetParameter("serverVoiceChat", "0");
        else if (profile.VoiceChatMode == ServerProfile.VoiceChatEnabled)
            section.SetParameter("serverVoiceChat", "1");
    }

    /// <summary>
    /// "Voice chat completely off" also disables Unreal's voice interface, so the server ignores voice packets.
    /// The overrides are removed again for the other modes.
    /// </summary>
    [IniSetting(Constants.FileIniServer, "Engine")]
    public static void ApplyVoiceSettings(AppSetup appSetup, ServerProfile profile, IniDocument document)
    {
        var disabled = profile.VoiceChatMode == ServerProfile.VoiceChatDisabled;
        SetOrRemove(document, "Voice", "bEnabled", disabled ? "False" : null);
        SetOrRemove(document, "OnlineSubsystem", "bHasVoiceEnabled", disabled ? "False" : null);
    }

    /// <summary>
    /// How far from a player a thrall still counts as relevant (<c>dw.ThrallMaxRelevantDistance</c>, in game units). Nothing is
    /// written for 0 and a line that was written before is removed, so the game keeps its own value.
    /// </summary>
    [IniSetting(Constants.FileIniServer, "Engine")]
    public static void ApplyThrallDistance(AppSetup appSetup, ServerProfile profile, IniDocument document)
    {
        var distance = profile.ThrallMaxRelevantDistance;
        SetOrRemove(document, "SystemSettings", "dw.ThrallMaxRelevantDistance",
            distance > 0 ? Math.Min(distance, ServerProfile.ThrallMaxRelevantDistanceMaximum).ToString(CultureInfo.InvariantCulture) : null);
    }

    private static void SetOrRemove(IniDocument document, string sectionName, string key, string? value)
    {
        var section = document.GetSection(sectionName);
        if (value is not null)
        {
            section.SetParameter(key, value);
            return;
        }
        section.GetParameters(key).ForEach(section.Remove);
        if (section.GetParameters().Count == 0)
            document.Remove(section);
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
