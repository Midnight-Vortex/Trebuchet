using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Styling;
using SteamWorksWebAPI;
using Trebuchet.Assets;
using TrebuchetLib;
using TrebuchetLib.Services;

namespace Trebuchet.Utils;

internal static class Utils
{
    public static void ApplyPlateformTheme(PlateformTheme theme)
    {
        if (Application.Current is null) return;
        
        switch (theme)
        {
            case PlateformTheme.Default:
                Application.Current.RequestedThemeVariant = ThemeVariant.Default;
                return;
            case PlateformTheme.Dark:
                Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
                return;
            case PlateformTheme.Light:
                Application.Current.RequestedThemeVariant = ThemeVariant.Light;
                return;
        }
    }

    [Localizable(false)]
    public static string? GetAutoStartValue(bool testLive)
        => GetAutoStartValue(testLive ? GameEdition.TestLive : GameEdition.Legacy);

    [Localizable(false)]
    public static string? GetAutoStartValue(GameEdition edition)
    {
        var process = Process.GetCurrentProcess().MainModule?.FileName;
        if (process is null) return null;

        return $"\"{process}\" {Constants.GetCliArg(edition)}";
    }
}