using System.Text.RegularExpressions;

namespace TrebuchetLib.DiscordBot;

public enum BotCustomIdKind
{
    /// <summary>A button of the status message: start, stop, restart, postpone, resume of one server.</summary>
    Action,
    Confirm,
    Cancel,
    /// <summary>The selection of the minutes of a postponement.</summary>
    PostponeSelect
}

/// <summary>
/// The IDs of buttons and menus. They are only a name: whoever sends one still goes through the full check, so a
/// forged or old ID can do nothing a click of that user could not do anyway.
/// </summary>
public static partial class BotCustomId
{
    public const int MaxLength = 100;
    private const string Prefix = "tb1";

    private static readonly (BotCommand command, string name)[] ActionNames =
    [
        (BotCommand.Start, "start"),
        (BotCommand.Stop, "stop"),
        (BotCommand.RestartNow, "restart"),
        (BotCommand.Postpone, "postpone"),
        (BotCommand.Resume, "resume")
    ];

    public static string ForAction(BotCommand command, int instance)
    {
        var entry = ActionNames.FirstOrDefault(x => x.command == command);
        if (entry.name is null) throw new ArgumentException($"{command} has no button", nameof(command));
        return $"{Prefix}:a:{entry.name}:{instance}";
    }

    public static string ForConfirm(string nonce) => $"{Prefix}:ok:{nonce}";
    public static string ForCancel(string nonce) => $"{Prefix}:no:{nonce}";
    public static string ForPostponeSelect(string nonce) => $"{Prefix}:pp:{nonce}";

    /// <summary>Strict: anything that is not exactly one of the forms above is rejected.</summary>
    public static bool TryParse(string? customId, int instanceCount, out BotCustomIdKind kind, out BotCommand command,
        out int instance, out string nonce)
    {
        kind = default;
        command = default;
        instance = -1;
        nonce = string.Empty;
        if (string.IsNullOrEmpty(customId) || customId.Length > MaxLength) return false;

        var action = ActionPattern().Match(customId);
        if (action.Success)
        {
            var name = action.Groups[1].Value;
            var entry = ActionNames.FirstOrDefault(x => x.name == name);
            if (entry.name is null) return false;
            if (!int.TryParse(action.Groups[2].Value, out instance) || instance < 0 || instance >= instanceCount) return false;
            kind = BotCustomIdKind.Action;
            command = entry.command;
            return true;
        }

        var question = QuestionPattern().Match(customId);
        if (!question.Success) return false;
        kind = question.Groups[1].Value switch
        {
            "ok" => BotCustomIdKind.Confirm,
            "no" => BotCustomIdKind.Cancel,
            _ => BotCustomIdKind.PostponeSelect
        };
        nonce = question.Groups[2].Value;
        return true;
    }

    [GeneratedRegex("^tb1:a:([a-z]{4,10}):([0-9]{1,3})$")]
    private static partial Regex ActionPattern();

    [GeneratedRegex("^tb1:(ok|no|pp):([A-Za-z0-9_-]{22})$")]
    private static partial Regex QuestionPattern();
}
