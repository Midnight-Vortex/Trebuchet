using System;
using System.Collections.Generic;
using System.Globalization;
using ReactiveUI;
using Trebuchet.Assets;
using Trebuchet.ViewModels.SettingFields;
using TrebuchetLib.DiscordBot;

namespace Trebuchet.ViewModels.DiscordBot;

/// <summary>A line of the "check security" list: green, yellow or red.</summary>
public sealed class SecurityCheckRow(BotCheckItem item)
{
    public BotCheckItem Item { get; } = item;
    public string Text { get; } = DiscordBotTexts.CheckLine(item);
    public bool IsOk => Item.Status == BotCheckStatus.Ok;
    public bool IsWarning => Item.Status == BotCheckStatus.Warning;
    public bool IsError => Item.Status is not (BotCheckStatus.Ok or BotCheckStatus.Warning);

    public string Icon => Item.Status switch
    {
        BotCheckStatus.Ok => @"mdi-check-circle",
        BotCheckStatus.Warning => @"mdi-alert",
        _ => @"mdi-close-circle"
    };
}

/// <summary>A line of the audit log. The texts are cleaned and without tokens already; cleaned again to be sure.</summary>
public sealed class AuditEntryRow(BotAuditEntry entry)
{
    public BotAuditEntry Entry { get; } = entry;

    public string Time { get; } = entry.UtcTime.ToLocalTime().ToString(@"yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public string Result { get; } = DiscordBotTexts.AuditResult(entry.Result);

    public string Text { get; } = string.Join(@"  ·  ", Parts(entry));

    public bool IsOk => Entry.Result is BotAuditResult.Allowed or BotAuditResult.Executed;
    public bool IsWarning => Entry.Result is BotAuditResult.Throttled or BotAuditResult.Cancelled;
    public bool IsError => !IsOk && !IsWarning;

    private static IEnumerable<string> Parts(BotAuditEntry entry)
    {
        var name = Clean(entry.UserName);
        yield return string.IsNullOrEmpty(name)
            ? entry.UserId.ToString(CultureInfo.InvariantCulture)
            : $@"{entry.UserId.ToString(CultureInfo.InvariantCulture)} ({name})";
        yield return entry.Command is { } command ? DiscordBotTexts.Command(command) : @"-";
        var parameters = Clean(entry.Parameters);
        if (parameters.Length > 0) yield return parameters;
        var detail = Clean(entry.Detail);
        if (detail.Length > 0) yield return detail;
    }

    private static string Clean(string? text) => BotInputRules.SanitizeForLog(BotLogRedactor.Redact(text));
}

/// <summary>
/// A server instance in the list of servers: whether the bot manages it. The name the bot shows is the one of the save
/// the instance runs (field "Server name" of the server save), it is not set here. The value lives in the editor;
/// setting it marks the settings as edited.
/// </summary>
public sealed class BotInstanceRow(int instance, DiscordBotSettingsEditor editor, int instanceCount, bool canEdit, Action edited) : ReactiveObject
{
    public int Instance { get; } = instance;
    public bool CanEdit { get; } = canEdit;
    public string Title => string.Format(CultureInfo.CurrentCulture, Resources.DiscordBotServerInstance, Instance);
    public bool IsManaged
    {
        get => editor.IsInstanceManaged(Instance);
        set
        {
            if (!CanEdit || value == editor.IsInstanceManaged(Instance)) return;
            editor.SetInstanceManaged(Instance, value, instanceCount);
            this.RaisePropertyChanged();
            edited();
        }
    }
}

/// <summary>The commands of one level with the note what the level means.</summary>
public sealed class BotCommandGroup(BotLevel level, IReadOnlyList<ToggleField> fields)
{
    public BotLevel Level { get; } = level;
    public string Title { get; } = DiscordBotTexts.Level(level);
    public string Note { get; } = DiscordBotTexts.LevelDescription(level);
    public bool IsRestricted => Level >= BotLevel.Critical;
    public IReadOnlyList<ToggleField> Fields { get; } = fields;
}
