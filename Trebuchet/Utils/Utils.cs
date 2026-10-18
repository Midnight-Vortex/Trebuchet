using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform;
using Avalonia.Styling;
using SteamWorksWebAPI;
using Trebuchet.Assets;
using TrebuchetLib;
using TrebuchetLib.Services;

namespace Trebuchet.Utils;

internal static class Utils
{
    public static async Task<bool> SetClipboardText(string text)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
            return false;
        if (window.Clipboard is not { } clipboard) return false;
        await clipboard.SetTextAsync(text);
        return true;
    }

    /// <summary>
    /// DSL "Copy Connection Information" text for a server instance.
    /// </summary>
    public static string BuildConnectionInfo(MacroContext context)
    {
        var lines = new List<string>
        {
            $@"Name: {ServerMacros.Expand(@"{servername}", context)}",
            $@"IP: {ServerMacros.Expand(@"{ip}:{game_port}", context)}",
            $@"Query: {ServerMacros.Expand(@"{ip}:{steam_port}", context)}"
        };
        if (!string.IsNullOrEmpty(context.Profile?.ServerPassword))
            lines.Add($@"Password: {context.Profile.ServerPassword}");
        lines.Add(ServerMacros.Expand(@"Version: {server_revision} ({engine_version}, {branch_name})", context));
        lines.Add(ServerMacros.Expand(@"{steamconnect}", context));
        return string.Join(Environment.NewLine, lines);
    }

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
    public static string? GetAutoStartValue(GameEdition edition)
    {
        var process = Process.GetCurrentProcess().MainModule?.FileName;
        if (process is null) return null;

        return $"\"{process}\" {Constants.GetCliArg(edition)}";
    }
}
