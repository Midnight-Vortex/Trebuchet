using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using tot_lib;
using tot_gui_lib;
using TrebuchetLib;
using TrebuchetLib.Services;

namespace Trebuchet.Utils;

public static class AppConstants
{
    public const string ConfigFileName = "settings.ui.json";
    public const string ConfigFileNameEnhanced = "settings.ui.enhanced.json";
    public const string ConfigFileNameTestLive = "settings.ui.testlive.json";
    public const string GithubOwnerUpdate = "Totchinuko";
    public const string GithubRepoUpdate = "Trebuchet";
    public const string AutoStartLive = "TotTrebuchetLive";
    public const string AutoStartEnhanced = "TotTrebuchetEnhanced";
    public const string AutoStartTestLive = "TotTrebuchetTestLive";

    public static string GetAutoStartName(GameEdition edition) => edition switch
    {
        GameEdition.Enhanced => AutoStartEnhanced,
        GameEdition.TestLive => AutoStartTestLive,
        _ => AutoStartLive
    };

    public const string RestartArg = "--restart";

    [Localizable(false)]
    public static readonly string[] UICultureList = ["en", "fr", "de"];

    /// <summary>Shared UI (language/theme) before an edition is chosen — Legacy file for backward compatibility.</summary>
    public static string GetUIConfigPath() => GetUIConfigPath(GameEdition.Legacy);

    /// <summary>Per-edition UI selections (profiles/modlists). Legacy keeps <see cref="ConfigFileName"/>.</summary>
    public static string GetUIConfigPath(GameEdition edition)
    {
        var folder = AppSetup.GetAppConfigDirectory();
        if (!folder.Exists)
            Directory.CreateDirectory(folder.FullName);
        var file = edition switch
        {
            GameEdition.Enhanced => ConfigFileNameEnhanced,
            GameEdition.TestLive => ConfigFileNameTestLive,
            _ => ConfigFileName
        };
        return Path.Combine(folder.FullName, file);
    }

    public static string GetUpdateContentType()
    {
        if (OperatingSystem.IsWindows()) return GithubUpdater.WindowsMimeType;
        return string.Empty;
    }
}