using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Trebuchet.Assets;
using Trebuchet.Utils;
using TrebuchetLib;

namespace Trebuchet.Services;

/// <summary>
/// Notification templates and player messages ship with a default text per UI language. A value that is still
/// one of the known defaults (any language) follows the current language; texts the user wrote are kept.
/// </summary>
public static class LocalizedDefaults
{
    private static readonly string[] KnownCultures = AppConstants.UICultureList;

    public static string Get(string key, string fallback)
    {
        return Resources.ResourceManager.GetString(key, Resources.Culture) ?? fallback;
    }

    private static bool IsKnownDefault(string key, string value, string libraryDefault)
    {
        // Empty is a deliberate choice ("no message" / "use the shared template"), never replaced.
        if (string.IsNullOrEmpty(value)) return false;
        if (value == libraryDefault) return true;
        return KnownCultures.Any(c => Resources.ResourceManager.GetString(key, CultureInfo.GetCultureInfo(c)) == value);
    }

    private static bool Localize(string key, string libraryDefault, Func<string> getter, Action<string> setter)
    {
        var current = getter();
        if (!IsKnownDefault(key, current, libraryDefault)) return false;
        var localized = Get(key, libraryDefault);
        if (current == localized) return false;
        setter(localized);
        return true;
    }

    /// <summary>Notification templates shared by all servers.</summary>
    public static IReadOnlyList<(string key, string libraryDefault, Func<Config, string> get, Action<Config, string> set)> ConfigTemplates { get; } =
    [
        (nameof(Resources.DefaultNotificationServerCrash), Config.NotificationServerCrashDefault, c => c.NotificationServerCrash, (c, v) => c.NotificationServerCrash = v),
        (nameof(Resources.DefaultNotificationServerOnline), Config.NotificationServerOnlineDefault, c => c.NotificationServerOnline, (c, v) => c.NotificationServerOnline = v),
        (nameof(Resources.DefaultNotificationServerStop), Config.NotificationServerStopDefault, c => c.NotificationServerStop, (c, v) => c.NotificationServerStop = v),
        (nameof(Resources.DefaultNotificationServerManualStop), Config.NotificationServerManualStopDefault, c => c.NotificationServerManualStop, (c, v) => c.NotificationServerManualStop = v),
        (nameof(Resources.DefaultNotificationServerManualRestart), Config.NotificationServerManualRestartDefault, c => c.NotificationServerManualRestart, (c, v) => c.NotificationServerManualRestart = v),
        (nameof(Resources.DefaultNotificationServerAutomatedRestart), Config.NotificationServerAutomatedRestartDefault, c => c.NotificationServerAutomatedRestart, (c, v) => c.NotificationServerAutomatedRestart = v),
        (nameof(Resources.DefaultNotificationServerModUpdate), Config.NotificationServerModUpdateDefault, c => c.NotificationServerModUpdate, (c, v) => c.NotificationServerModUpdate = v),
        (nameof(Resources.DefaultNotificationServerServerUpdate), Config.NotificationServerServerUpdateDefault, c => c.NotificationServerServerUpdate, (c, v) => c.NotificationServerServerUpdate = v),
        (nameof(Resources.DefaultNotificationExternalIpChanged), Config.NotificationExternalIpChangedDefault, c => c.NotificationExternalIpChanged, (c, v) => c.NotificationExternalIpChanged = v),
        (nameof(Resources.DefaultNotificationCrashLoop), Config.NotificationCrashLoopDefault, c => c.NotificationCrashLoop, (c, v) => c.NotificationCrashLoop = v),
        (nameof(Resources.DefaultNotificationModUpdateDetected), Config.NotificationModUpdateDetectedDefault, c => c.NotificationModUpdateDetected, (c, v) => c.NotificationModUpdateDetected = v),
        (nameof(Resources.DefaultNotificationTimedRestart), Config.NotificationTimedRestartDefault, c => c.NotificationTimedRestart, (c, v) => c.NotificationTimedRestart = v),
        (nameof(Resources.DefaultNotificationTimedStop), Config.NotificationTimedStopDefault, c => c.NotificationTimedStop, (c, v) => c.NotificationTimedStop = v),
    ];

    /// <returns>true when a template was switched to the current language (the config needs saving).</returns>
    public static bool LocalizeConfig(Config config)
    {
        var changed = false;
        foreach (var (key, libraryDefault, get, set) in ConfigTemplates)
            changed |= Localize(key, libraryDefault, () => get(config), v => set(config, v));
        return changed;
    }

    public static string RestartedMessage => Get(nameof(Resources.DefaultMessageRestarted), ServerProfile.MessageRestartedDefault);

    public static List<RestartWarning> RestartWarnings
    {
        get
        {
            var defaults = ServerProfile.RestartWarningsDefault;
            string[] keys = [nameof(Resources.DefaultRestartWarning1), nameof(Resources.DefaultRestartWarning2), nameof(Resources.DefaultRestartWarning3)];
            for (var i = 0; i < defaults.Count && i < keys.Length; i++)
                defaults[i].Message = Get(keys[i], defaults[i].Message);
            return defaults;
        }
    }

    /// <returns>true when a default message of the profile was switched to the current language.</returns>
    public static bool LocalizeProfile(ServerProfile profile)
    {
        var changed = Localize(nameof(Resources.DefaultMessageRestarted), ServerProfile.MessageRestartedDefault,
            () => profile.MessageRestarted, v => profile.MessageRestarted = v);

        var libraryDefaults = ServerProfile.RestartWarningsDefault;
        string[] keys = [nameof(Resources.DefaultRestartWarning1), nameof(Resources.DefaultRestartWarning2), nameof(Resources.DefaultRestartWarning3)];
        foreach (var warning in profile.RestartWarnings)
        {
            for (var i = 0; i < keys.Length && i < libraryDefaults.Count; i++)
            {
                var index = i;
                if (Localize(keys[index], libraryDefaults[index].Message, () => warning.Message, v => warning.Message = v))
                {
                    changed = true;
                    break;
                }
            }
        }
        return changed;
    }
}
