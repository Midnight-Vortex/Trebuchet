using System.Globalization;
using System.Reflection;
using TrebuchetLib.Services;
using Yuu.Ini;

namespace TrebuchetLib.YuuIni;

public static class YuuIniClientFiles
{
    public static async Task WriteIni(this AppSetup setup, ClientProfile profile)
    {
        Dictionary<string, IniDocument> documents = new Dictionary<string, IniDocument>();
        // Modify the default SectionName parse because funcom sometime does an oupsi and generate sections with an empty name
        var iniParserConfiguration = new IniParserConfiguration();
        iniParserConfiguration.SectionNameRegex = "\\s*[^\\[\\]]*\\s*";

        foreach (var method in GetIniMethods())
        {
            IniSettingAttribute attr = method.GetCustomAttribute<IniSettingAttribute>() ?? throw new Exception($"{method.Name} does not have IniSettingAttribute.");
            var relativePath = ResolveClientIniPath(setup, attr.Path);
            if (!documents.TryGetValue(relativePath, out IniDocument? document))
            {
                var iniPath = ResolveClientAbsolutePath(setup, relativePath);
                var iniContent = await Tools.GetFileContent(iniPath);
                document = IniParser.Parse(iniContent, iniParserConfiguration);
                documents.Add(relativePath, document);
            }
            method.Invoke(null, [setup, profile, document]);
        }

        foreach (var document in documents)
        {
            document.Value.MergeDuplicateSections();
            await Tools.SetFileContent(ResolveClientAbsolutePath(setup, document.Key), document.Value.ToString());
        }
    }

    public static async Task WriteLastConnection(this AppSetup setup, ClientConnection connection)
    {
        // Modify the default SectionName parse because funcom sometime does an oupsi and generate sections with an empty name
        var iniParserConfiguration = new IniParserConfiguration();
        iniParserConfiguration.SectionNameRegex = "\\s*[^\\[\\]]*\\s*";
        var iniPath = ResolveClientAbsolutePath(setup, string.Format(Constants.GetFileIniUser(setup.Edition), "Game"));
        var iniContent = await Tools.GetFileContent(iniPath);
        var document = IniParser.Parse(iniContent, iniParserConfiguration);
        
        document.GetSection("SavedServers")
            .SetParameter("LastConnected", $"{connection.IpAddress}:{connection.Port}");
        document.GetSection("SavedServers")
            .SetParameter("LastPassword", connection.Password);
        document.GetSection("SavedCoopData")
            .SetParameter("StartedListenServerSession", "False");
        await Tools.SetFileContent(iniPath, document.ToString());
    }
    
    [IniSetting(Constants.FileIniDefault, "Engine")]
    public static void DefaultEngine(AppSetup setup, ClientProfile profile, IniDocument document)
    {
        document.GetSection("/Game/Mods/TotAdmin/PreLoad/Tot_W_NoServer.Tot_W_NoServer_C").SetParameter("NoServerListAutoRefresh", profile.TotAdminDoNotLoadServerList ? "true" : "false");
    }

    [IniSetting(Constants.FileIniUser, "GraniteBaking")]
    public static void Granite(AppSetup setup, ClientProfile profile, IniDocument document)
    {
        document.GetSection("/script/granitematerialbaker.granitebakingsettings")
            .SetParameter("Quality", profile.UltraAnisotropy ? "High" : "Medium");
    }

    [IniSetting(Constants.FileIniDefault, "Game")]
    public static void SkipMovies(AppSetup setup, ClientProfile profile, IniDocument document)
    {
        IniSection section = document.GetSection("/Script/MoviePlayer.MoviePlayerSettings");
        section.GetParameters("+StartupMovies").ForEach(section.Remove);
        if (!profile.RemoveIntroVideo)
        {
            section.AddParameter("+StartupMovies", "StartupUE4");
            section.AddParameter("+StartupMovies", "StartupNvidia");
            section.AddParameter("+StartupMovies", "CinematicIntroV2");
        }
        else
        {
            section.SetParameter("bWaitForMoviesToComplete", "True");
            section.SetParameter("bMoviesAreSkippable", "True");
        }
    }

    [IniSetting(Constants.FileIniUser, "Engine")]
    public static void SoundSettings(AppSetup setup, ClientProfile profile, IniDocument document)
    {
        document.GetSection("Audio").SetParameter("UnfocusedVolumeMultiplier", profile.BackgroundSound ? "1.0" : "0,0");

        IniSection section = document.GetSection("Core.Log");
        section.GetParameters().ForEach(section.Remove);

        var enableAsyncScene = setup.Experiment
            ? profile.EnableAsyncScene
            : ClientProfile.EnableAsyncSceneDefault;
        document.GetSection("/script/engine.physicssettings")
            .SetParameter("bEnableAsyncScene", enableAsyncScene ? "True" : "False");

        if (profile.LogFilters.Count > 0)
            foreach (string filter in profile.LogFilters)
            {
                string[] content = filter.Split('=', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                section.AddParameter(content[0], content[1]);
            }
        else
            document.Remove(section);
    }

    [IniSetting(Constants.FileIniDefault, "Scalability")]
    public static void UltraSetting(AppSetup setup, ClientProfile profile, IniDocument document)
    {
        document.GetSection("TextureQuality@3").SetParameter("r.Streaming.PoolSize", (1500 + profile.AddedTexturePool).ToString());
        document.GetSection("TextureQuality@3").SetParameter("r.MaxAnisotropy", profile.UltraAnisotropy ? "16" : "8");
    }

    [IniSetting(Constants.FileIniUser, "Game")]
    public static void UserGameSetting(AppSetup setup, ClientProfile profile, IniDocument document)
    {
        document.GetSection("/script/engine.player")
            .SetParameter("ConfiguredInternetSpeed", 
                setup.Experiment 
                    ? profile.ConfiguredInternetSpeed.ToString()
                    : ClientProfile.ConfiguredInternetSpeedDefault.ToString()
                    );
    }
    
    private static IEnumerable<MethodInfo> GetIniMethods()
    {
        return typeof(YuuIniClientFiles).GetMethods()
            .Where(meth => meth.GetCustomAttributes(typeof(IniSettingAttribute), true).Any())
            .Where(meth =>
                meth.GetParameters().Length == 3 && 
                meth.GetParameters()[0].ParameterType == typeof(AppSetup) &&
                meth.GetParameters()[1].ParameterType == typeof(ClientProfile) &&
                meth.GetParameters()[2].ParameterType == typeof(IniDocument)
                );
    }

    /// <summary>
    /// Resolves a client-relative path to an absolute path, following junction chains under Saved
    /// so file I/O avoids untrusted mount-point traversal (WinError 448).
    /// </summary>
    private static string ResolveClientAbsolutePath(AppSetup setup, string relativePath)
    {
        var clientFolder = Path.GetFullPath(setup.GetClientFolder());
        var full = Path.GetFullPath(Path.Combine(clientFolder, relativePath));
        var savedRoot = Path.GetFullPath(Path.Combine(clientFolder, Constants.FolderGameSave));

        var relativeToSaved = Path.GetRelativePath(savedRoot, full);
        if (relativeToSaved == "." || (!relativeToSaved.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relativeToSaved)))
        {
            var physical = Tools.ResolveReparsePointChain(savedRoot);
            var resolved = relativeToSaved == "."
                ? physical
                : Path.Combine(physical, relativeToSaved);
            return Tools.ResolveReparsePointsInPath(resolved);
        }

        return Tools.ResolveReparsePointsInPath(full);
    }

    /// <summary>
    /// Attributes still use <see cref="Constants.FileIniUser"/> (WindowsNoEditor).
    /// Enhanced client configs live under Saved\Config\Windows\.
    /// </summary>
    private static string ResolveClientIniPath(AppSetup setup, string attributePath)
    {
        if (setup.Edition != GameEdition.Enhanced)
            return attributePath;

        const string legacyFolder = "WindowsNoEditor";
        const string enhancedFolder = "Windows";
        if (attributePath.Contains(legacyFolder, StringComparison.OrdinalIgnoreCase))
            return attributePath.Replace(legacyFolder, enhancedFolder, StringComparison.OrdinalIgnoreCase);

        return attributePath;
    }
}