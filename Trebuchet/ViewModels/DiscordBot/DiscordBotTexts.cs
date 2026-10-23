using System;
using System.Collections.Generic;
using Trebuchet.Assets;
using TrebuchetLib.DiscordBot;

namespace Trebuchet.ViewModels.DiscordBot;

/// <summary>The texts of the panel for the states, problems, issues and checks of the bot (Resources, every UI language).</summary>
public static class DiscordBotTexts
{
    public static string State(DiscordBotState state) => state switch
    {
        DiscordBotState.Stopped => Resources.DiscordBotStateStopped,
        DiscordBotState.Starting => Resources.DiscordBotStateStarting,
        DiscordBotState.Connected => Resources.DiscordBotStateConnected,
        DiscordBotState.Stopping => Resources.DiscordBotStateStopping,
        DiscordBotState.Error => Resources.DiscordBotStateError,
        _ => state.ToString()
    };

    /// <summary>A readable reason. Empty for <see cref="DiscordBotProblem.None"/>.</summary>
    public static string Problem(DiscordBotProblem problem) => problem switch
    {
        DiscordBotProblem.None => string.Empty,
        DiscordBotProblem.NoToken => Resources.DiscordBotProblemNoToken,
        DiscordBotProblem.InvalidSettings => Resources.DiscordBotProblemInvalidSettings,
        DiscordBotProblem.TokenInUse => Resources.DiscordBotProblemTokenInUse,
        DiscordBotProblem.LoginFailed => Resources.DiscordBotProblemLoginFailed,
        DiscordBotProblem.GuildNotFound => Resources.DiscordBotProblemGuildNotFound,
        DiscordBotProblem.ConnectionLost => Resources.DiscordBotProblemConnectionLost,
        _ => Resources.DiscordBotProblemUnknown
    };

    /// <summary>What is wrong with the settings, with the field it is about. Never repeats a secret.</summary>
    public static string Issue(BotConfigIssue issue)
    {
        var text = issue.Kind switch
        {
            BotConfigIssueKind.GuildMissing => Resources.DiscordBotIssueGuildMissing,
            BotConfigIssueKind.SuperAdminMissing => Resources.DiscordBotIssueSuperAdminMissing,
            BotConfigIssueKind.InvalidId => Resources.DiscordBotIssueInvalidId,
            BotConfigIssueKind.NoRoles => Resources.DiscordBotIssueNoRoles,
            BotConfigIssueKind.EveryoneRole => Resources.DiscordBotIssueEveryoneRole,
            BotConfigIssueKind.InvalidLimit => Resources.DiscordBotIssueInvalidLimit,
            BotConfigIssueKind.InvalidPostponeChoices => Resources.DiscordBotIssueInvalidPostponeChoices,
            BotConfigIssueKind.UnknownCommand => Resources.DiscordBotIssueUnknownCommand,
            _ => issue.Kind.ToString()
        };
        // The list of choices is shown in its own field; repeating it adds nothing.
        if (issue.Kind == BotConfigIssueKind.InvalidPostponeChoices || string.IsNullOrWhiteSpace(issue.Detail))
            return text;
        return text + @": " + LocalizeDetail(BotLogRedactor.Redact(issue.Detail));
    }

    /// <summary>"CooldownSeconds: 0 (1-300)" → "Wait between two commands (seconds): 0 (1-300)".</summary>
    public static string LocalizeDetail(string detail)
    {
        var colon = detail.IndexOf(':');
        var field = colon < 0 ? detail.Trim() : detail[..colon].Trim();
        var label = FieldLabel(field);
        if (label is null) return detail;
        return colon < 0 ? label : label + detail[colon..];
    }

    /// <summary>The label the panel shows for a setting, null for an unknown name.</summary>
    public static string? FieldLabel(string field) => field switch
    {
        nameof(DiscordBotConfig.GuildId) => Resources.DiscordBotGuild,
        nameof(DiscordBotConfig.SuperAdminUserId) => Resources.DiscordBotSuperAdmin,
        nameof(DiscordBotConfig.AllowedUserIds) => Resources.DiscordBotUsers,
        nameof(DiscordBotConfig.AllowedRoleIds) => Resources.DiscordBotRoles,
        nameof(DiscordBotConfig.AllowedChannelIds) => Resources.DiscordBotAdminChannel,
        nameof(DiscordBotConfig.AuditChannelId) => Resources.DiscordBotAuditChannel,
        nameof(DiscordBotConfig.CooldownSeconds) => Resources.DiscordBotCooldown,
        nameof(DiscordBotConfig.CriticalLimit) => Resources.DiscordBotCriticalLimit,
        nameof(DiscordBotConfig.CriticalWindowMinutes) => Resources.DiscordBotCriticalWindow,
        nameof(DiscordBotConfig.LockoutAttempts) => Resources.DiscordBotLockoutAttempts,
        nameof(DiscordBotConfig.LockoutMinutes) => Resources.DiscordBotLockoutMinutes,
        nameof(DiscordBotConfig.MaxPauseMinutes) => Resources.DiscordBotMaxPause,
        nameof(DiscordBotConfig.ActionDelayMinutes) => Resources.DiscordBotActionDelay,
        nameof(DiscordBotConfig.PostponeChoices) => Resources.DiscordBotPostponeChoices,
        _ => null
    };

    public static string IdInput(BotIdInputResult result) => result switch
    {
        BotIdInputResult.Added => string.Empty,
        BotIdInputResult.Invalid => Resources.DiscordBotIdInvalid,
        BotIdInputResult.Duplicate => Resources.DiscordBotIdDuplicate,
        BotIdInputResult.EveryoneRole => Resources.DiscordBotIdEveryone,
        BotIdInputResult.LooksLikeToken => Resources.DiscordBotIdLooksLikeToken,
        _ => Resources.DiscordBotIdInvalid
    };

    public static string Check(BotCheckKind kind, BotCheckStatus status) => (kind, status) switch
    {
        (BotCheckKind.Token, BotCheckStatus.Ok) => Resources.DiscordBotCheckTokenOk,
        (BotCheckKind.Token, BotCheckStatus.Warning) => Resources.DiscordBotCheckTokenWarning,
        (BotCheckKind.Token, BotCheckStatus.Error) => Resources.DiscordBotCheckTokenError,
        (BotCheckKind.Settings, BotCheckStatus.Ok) => Resources.DiscordBotCheckSettingsOk,
        (BotCheckKind.Settings, BotCheckStatus.Warning) => Resources.DiscordBotCheckSettingsWarning,
        (BotCheckKind.Settings, BotCheckStatus.Error) => Resources.DiscordBotCheckSettingsError,
        (BotCheckKind.Guild, BotCheckStatus.Ok) => Resources.DiscordBotCheckGuildOk,
        (BotCheckKind.Guild, BotCheckStatus.Warning) => Resources.DiscordBotCheckGuildWarning,
        (BotCheckKind.Guild, BotCheckStatus.Error) => Resources.DiscordBotCheckGuildError,
        (BotCheckKind.Roles, BotCheckStatus.Ok) => Resources.DiscordBotCheckRolesOk,
        (BotCheckKind.Roles, BotCheckStatus.Warning) => Resources.DiscordBotCheckRolesWarning,
        (BotCheckKind.Roles, BotCheckStatus.Error) => Resources.DiscordBotCheckRolesError,
        (BotCheckKind.EveryoneRole, BotCheckStatus.Ok) => Resources.DiscordBotCheckEveryoneRoleOk,
        (BotCheckKind.EveryoneRole, BotCheckStatus.Warning) => Resources.DiscordBotCheckEveryoneRoleWarning,
        (BotCheckKind.EveryoneRole, BotCheckStatus.Error) => Resources.DiscordBotCheckEveryoneRoleError,
        (BotCheckKind.OtherGuilds, BotCheckStatus.Ok) => Resources.DiscordBotCheckOtherGuildsOk,
        (BotCheckKind.OtherGuilds, BotCheckStatus.Warning) => Resources.DiscordBotCheckOtherGuildsWarning,
        (BotCheckKind.OtherGuilds, BotCheckStatus.Error) => Resources.DiscordBotCheckOtherGuildsError,
        (BotCheckKind.AdministratorPermission, BotCheckStatus.Ok) => Resources.DiscordBotCheckAdministratorPermissionOk,
        (BotCheckKind.AdministratorPermission, BotCheckStatus.Warning) => Resources.DiscordBotCheckAdministratorPermissionWarning,
        (BotCheckKind.AdministratorPermission, BotCheckStatus.Error) => Resources.DiscordBotCheckAdministratorPermissionError,
        (BotCheckKind.Channel, BotCheckStatus.Ok) => Resources.DiscordBotCheckChannelOk,
        (BotCheckKind.Channel, BotCheckStatus.Warning) => Resources.DiscordBotCheckChannelWarning,
        (BotCheckKind.Channel, BotCheckStatus.Error) => Resources.DiscordBotCheckChannelError,
        (BotCheckKind.Connection, BotCheckStatus.Ok) => Resources.DiscordBotCheckConnectionOk,
        (BotCheckKind.Connection, BotCheckStatus.Warning) => Resources.DiscordBotCheckConnectionWarning,
        (BotCheckKind.Connection, BotCheckStatus.Error) => Resources.DiscordBotCheckConnectionError,
        _ => $@"{kind}: {status}"
    };

    /// <summary>The line of the check list: the text of the panel, then the short detail of the bot (never a token).</summary>
    public static string CheckLine(BotCheckItem item)
    {
        var text = Check(item.Kind, item.Status);
        var detail = BotInputRules.SanitizeForLog(BotLogRedactor.Redact(item.Detail));
        return string.IsNullOrEmpty(detail) ? text : $@"{text} ({detail})";
    }

    public static string Command(BotCommand command) => command switch
    {
        BotCommand.Status => Resources.DiscordBotCommandStatus,
        BotCommand.Postpone => Resources.DiscordBotCommandPostpone,
        BotCommand.Resume => Resources.DiscordBotCommandResume,
        BotCommand.RestartNow => Resources.DiscordBotCommandRestartNow,
        BotCommand.Stop => Resources.DiscordBotCommandStop,
        BotCommand.Start => Resources.DiscordBotCommandStart,
        BotCommand.Update => Resources.DiscordBotCommandUpdate,
        BotCommand.SetChannel => Resources.DiscordBotCommandSetChannel,
        _ => command.ToString()
    };

    public static string CommandDescription(BotCommand command) => command switch
    {
        BotCommand.Status => Resources.DiscordBotCommandStatusText,
        BotCommand.Postpone => Resources.DiscordBotCommandPostponeText,
        BotCommand.Resume => Resources.DiscordBotCommandResumeText,
        BotCommand.RestartNow => Resources.DiscordBotCommandRestartNowText,
        BotCommand.Stop => Resources.DiscordBotCommandStopText,
        BotCommand.Start => Resources.DiscordBotCommandStartText,
        BotCommand.Update => Resources.DiscordBotCommandUpdateText,
        BotCommand.SetChannel => Resources.DiscordBotCommandSetChannelText,
        _ => string.Empty
    };

    public static string Level(BotLevel level) => level switch
    {
        BotLevel.Read => Resources.DiscordBotLevelRead,
        BotLevel.Manage => Resources.DiscordBotLevelManage,
        BotLevel.Critical => Resources.DiscordBotLevelCritical,
        BotLevel.SuperAdmin => Resources.DiscordBotLevelSuperAdmin,
        _ => level.ToString()
    };

    public static string LevelDescription(BotLevel level) => level switch
    {
        BotLevel.Read => Resources.DiscordBotLevelReadText,
        BotLevel.Manage => Resources.DiscordBotLevelManageText,
        BotLevel.Critical => Resources.DiscordBotLevelCriticalText,
        BotLevel.SuperAdmin => Resources.DiscordBotLevelSuperAdminText,
        _ => string.Empty
    };

    public static string AuditResult(BotAuditResult result) => result switch
    {
        BotAuditResult.Allowed => Resources.DiscordBotAuditResultAllowed,
        BotAuditResult.Denied => Resources.DiscordBotAuditResultDenied,
        BotAuditResult.Throttled => Resources.DiscordBotAuditResultThrottled,
        BotAuditResult.LockedOut => Resources.DiscordBotAuditResultLockedOut,
        BotAuditResult.Executed => Resources.DiscordBotAuditResultExecuted,
        BotAuditResult.Failed => Resources.DiscordBotAuditResultFailed,
        BotAuditResult.Cancelled => Resources.DiscordBotAuditResultCancelled,
        _ => result.ToString()
    };

    /// <summary>The checklist of the setup (plan 1.8, docs/INSTALLME.md).</summary>
    public static IReadOnlyList<string> SetupSteps() =>
    [
        Resources.DiscordBotSetupStep1,
        Resources.DiscordBotSetupStep2,
        Resources.DiscordBotSetupStep3,
        Resources.DiscordBotSetupStep4,
        Resources.DiscordBotSetupStep5,
        Resources.DiscordBotSetupStep6,
        Resources.DiscordBotSetupStep7,
        Resources.DiscordBotSetupStep8,
        Resources.DiscordBotSetupStep9
    ];

    public static Exception ServiceUnavailable() => new InvalidOperationException(Resources.DiscordBotServiceError);
}
