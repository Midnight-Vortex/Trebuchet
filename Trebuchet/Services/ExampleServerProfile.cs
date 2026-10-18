using System;
using System.IO;
using System.Linq;
using Trebuchet.Assets;
using TrebuchetLib;
using TrebuchetLib.Sequences;
using TrebuchetLib.Services;

namespace Trebuchet.Services;

/// <summary>
/// Server save with every option filled in, so users can see how the options work together and copy it.
/// Created once in the UI language; an existing example (any language) is never overwritten.
/// </summary>
public static class ExampleServerProfile
{
    /// <summary>Not a valid webhook: Trebuchet skips it until the user enters a real URL.</summary>
    public const string DiscordWebHookPlaceholder = "https://discord.com/api/webhooks/YOUR_WEBHOOK_ID/YOUR_WEBHOOK_TOKEN";

    /// <returns>The created save, or null when an example save already exists.</returns>
    public static ServerProfileRef? CreateIfMissing(IAppFileHandler<ServerProfile, ServerProfileRef> handler)
    {
        if (Constants.ExampleServerProfileNames.Any(name => Directory.Exists(Path.Combine(handler.GetBaseFolder(), name))))
            return null;

        var reference = handler.Ref(Resources.ExampleProfileName);
        var profile = handler.Create(reference);
        var folder = Path.GetDirectoryName(handler.GetPath(reference)) ?? handler.GetBaseFolder();

        var script = Path.Combine(folder, Resources.ExampleScriptFileName);
        File.WriteAllText(script, string.Join("\r\n",
            "@echo off",
            "rem Example script: runs after every shutdown, after the backup (script mode).",
            "rem Arguments from the profile: %1 = server name, %2 = instance",
            "echo %date% %time% Server \"%~1\" (instance %2) stopped, backup done >> \"%~dp0script-log.txt\"",
            "exit /b 0",
            ""));

        profile.ServerName = Resources.ExampleServerName;
        profile.ServerPassword = Resources.ExampleServerPassword;
        profile.AdminPassword = Resources.ExampleAdminPassword;
        profile.MaxPlayers = 40;
        profile.SudoSuperAdmins = ["76561190000000000"];

        profile.EnableRCon = true;
        profile.RConPort = 25575;
        profile.RConPassword = Resources.ExampleRConPassword;
        profile.RConBroadcastMethod = 0;

        profile.EnableMultiHome = false;
        profile.MultiHomeAddress = "192.168.1.10";
        profile.EnableMultiHomeHttp = true;

        profile.KillZombies = true;
        profile.ZombieCheckSeconds = 300;
        profile.RestartWhenDown = true;
        profile.UseAllCores = true;
        profile.ProcessPriority = 1;
        profile.EnforceProcessSettings = true;
        profile.AdditionalArguments = "-nosound";
        profile.VoiceChatMode = ServerProfile.VoiceChatDisabled;
        profile.LogFilters = [..ServerProfile.LogFiltersDefault];
        LogFilterRules.EnsureRequiredServerFilters(profile.LogFilters);

        profile.AutoRestart = true;
        profile.AutoRestartMinUptime = TimeSpan.FromHours(2);
        profile.AutoRestartDailyTime = [TimeSpan.FromHours(6), TimeSpan.FromHours(18)];
        profile.AutoRestartMaxPerDay = 2;
        profile.RequiresRestartFileCheck = true;

        profile.UseRestartWarnings = true;
        profile.RestartWarnings = [new RestartWarning(TimeSpan.FromMinutes(15), Resources.ExampleWarning15), ..LocalizedDefaults.RestartWarnings];
        profile.RestartEarlyWhenEmpty = true;
        profile.MessageReady = Resources.ExampleMessageReady;
        profile.MessageRestarted = LocalizedDefaults.RestartedMessage;
        profile.MessageShutdown = Resources.ExampleMessageShutdown;

        profile.BackupOnShutdown = true;
        profile.BackupDirectory = @"%USERPROFILE%\Documents\Conan-Backups\" + reference.Name;
        profile.BackupLimitEnabled = true;
        profile.BackupLimitDays = 14;
        profile.ScriptMode = ServerProfile.ScriptModeShutdownAfterBackup;
        profile.ScriptPath = script;
        profile.ScriptArguments = "\"{servername}\" {instance}";

        profile.DiscordWebHookNotifications = DiscordWebHookPlaceholder;
        profile.DiscordBotName = "{servername}";
        profile.DiscordTimestamp = true;
        profile.NotifyExternalIpChange = true;

        // A custom stopping sequence would replace the restart warnings, so only the starting sequence is used.
        profile.StoppingSequence = new Sequence();
        profile.StartingSequence = new Sequence
        {
            Actions =
            [
                new SequenceMainAction(),
                new SequenceActionWaitOnline { TimeOut = TimeSpan.FromMinutes(10) },
                new SequenceActionBroadcast { Message = Resources.ExampleMessageOnline, SendRCon = true, SendDiscord = false },
                new SequenceActionDiscordWebHook { Message = Resources.ExampleDiscordMessage.ReplaceLineEndings("\n"), DiscordWebHook = DiscordWebHookPlaceholder },
            ]
        };
        profile.SaveFile();

        File.WriteAllText(Path.Combine(folder, Resources.ExampleReadmeFileName), Resources.ExampleReadme.ReplaceLineEndings("\r\n"));
        return reference;
    }
}
