using System.Text.Json.Serialization;

namespace TrebuchetLib.DiscordBot;

/// <summary>
/// Settings of the Discord bot, one file per game edition next to settings.&lt;edition&gt;.json. It holds no secret:
/// the token lives in its own protected file (<see cref="BotTokenStore"/>). Everything that is not valid or not set
/// means the bot does not start and nobody is allowed anything (see <see cref="BotPolicy"/>).
/// </summary>
public sealed class DiscordBotConfig : ConfigFile<DiscordBotConfig>
{
    /// <summary>No postponement is longer than a day, so updates are never put off for good.</summary>
    public const int HardMaxPauseMinutes = 24 * 60;
    /// <summary>The most critical actions per server in the window of the settings that can be set.</summary>
    public const int MaxCriticalLimit = 50;
    /// <summary>Critical actions per server in the window by default (it was 3, which was reached while setting the bot up).</summary>
    public const int DefaultCriticalLimit = 10;
    public const int DefaultCommunityLeadMinutes = 30;
    /// <summary>The status of a server knows a scheduled restart one hour ahead, so the community can not be told earlier.</summary>
    public const int MaxCommunityLeadMinutes = 60;
    /// <summary>A webhook name is at most 80 characters in Discord.</summary>
    public const int MaxIdentityNameLength = 80;
    /// <summary>The name of a bot account has 2 to 32 characters.</summary>
    public const int MinBotUsernameLength = 2;
    public const int MaxBotUsernameLength = 32;
    public const int MaxAvatarUrlLength = 512;
    // The limits of files written before the limit was raised: only these are replaced by the new default.
    private const int OldDefaultCriticalLimit = 3;
    private const int OldDefaultCriticalWindowMinutes = 10;
    private const int LimitsVersionCurrent = 3;
    public const int MaxPostponeChoices = 25;

    // IDs are written as text: JSON editors and scripts lose digits of numbers above 2^53. Both forms are read.
    private const JsonNumberHandling IdHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString;

    // A hand edited file may say null: that means "nothing", never a crash and never a default that allows more.
    private List<ulong> _allowedUserIds = [];
    private List<ulong> _allowedRoleIds = [];
    private List<ulong> _allowedChannelIds = [];
    private List<int> _postponeChoices = [15, 30, 60, 120, 180, 360];
    private Dictionary<int, ulong> _statusMessageIds = [];
    private List<string> _enabledCommands = BotCommands.EnabledByDefault.Select(x => x.ToString()).ToList();
    private string _language = "de";

    /// <summary>Start the bot together with Trebuchet. Off until someone switches it on.</summary>
    public bool AutoStart { get; set; }
    /// <summary>Language of the messages of the bot: "de" or "en".</summary>
    public string Language
    {
        get => _language;
        set => _language = string.IsNullOrWhiteSpace(value) ? "de" : value;
    }

    /// <summary>The only Discord server (guild) the bot works for. It leaves every other one.</summary>
    [JsonNumberHandling(IdHandling)]
    public ulong GuildId { get; set; }
    /// <summary>
    /// The one user who can not be removed and the only one who may use the super admin commands. Required.
    /// Like everyone else this user needs one of the allowed roles.
    /// </summary>
    [JsonNumberHandling(IdHandling)]
    public ulong SuperAdminUserId { get; set; }
    /// <summary>More users who may use the bot. They also need one of the allowed roles.</summary>
    [JsonNumberHandling(IdHandling)]
    public List<ulong> AllowedUserIds
    {
        get => _allowedUserIds;
        set => _allowedUserIds = value ?? [];
    }
    /// <summary>Roles that may use the bot. A user needs one of them and has to be on the list of users.</summary>
    [JsonNumberHandling(IdHandling)]
    public List<ulong> AllowedRoleIds
    {
        get => _allowedRoleIds;
        set => _allowedRoleIds = value ?? [];
    }
    /// <summary>Channels the bot answers in. Empty: every channel. Set with the channel command from the channel itself.</summary>
    [JsonNumberHandling(IdHandling)]
    public List<ulong> AllowedChannelIds
    {
        get => _allowedChannelIds;
        set => _allowedChannelIds = value ?? [];
    }
    /// <summary>Optional channel the bot writes everything it does into. 0: none (the audit webhook alone also works).</summary>
    [JsonNumberHandling(IdHandling)]
    public ulong AuditChannelId { get; set; }

    /// <summary>
    /// The name the audit log is written under. It only applies through the audit webhook (a bot account can not take another
    /// name per message) and replaces the name set for the webhook in Discord. Empty: the one of the webhook.
    /// </summary>
    public string AuditName { get; set; } = string.Empty;
    /// <summary>The picture (an https link to an image) of the audit log, like <see cref="AuditName"/>. Empty: the one of the webhook.</summary>
    public string AuditAvatarUrl { get; set; } = string.Empty;

    /// <summary>The community log: what the players of the server see (restart announced, restarting, online again).</summary>
    public bool CommunityEnabled { get; set; }
    /// <summary>The name of the community log, e.g. the name of the server community. Empty: the name of the webhook.</summary>
    public string CommunityName { get; set; } = string.Empty;
    /// <summary>The picture (an https link to an image) of the community log. Empty: the one of the webhook.</summary>
    public string CommunityAvatarUrl { get; set; } = string.Empty;
    /// <summary>How long before a planned restart or stop the community is told (minutes).</summary>
    public int CommunityLeadMinutes { get; set; } = DefaultCommunityLeadMinutes;
    /// <summary>
    /// The bot account itself takes the name and picture of the community log: the status message with its buttons and the answers
    /// are written by the bot account, and only a webhook can have another name per message. Discord allows two changes of the name
    /// an hour, and the name is the one of the bot on every server it is on.
    /// </summary>
    public bool BotTakesCommunityIdentity { get; set; }

    /// <summary>
    /// The mod update log: tells the players when a mod of the server mod lists got an update, with the notes of its author, through a
    /// webhook of its own (also of another Discord server).
    /// </summary>
    public bool ModUpdatesEnabled { get; set; }
    /// <summary>The name of the mod update log. Empty: the name of the webhook.</summary>
    public string ModUpdatesName { get; set; } = string.Empty;
    /// <summary>The picture (an https link to an image) of the mod update log. Empty: the one of the webhook.</summary>
    public string ModUpdatesAvatarUrl { get; set; } = string.Empty;

    /// <summary>Names of the commands that are switched on. A command missing here is off.</summary>
    public List<string> EnabledCommands
    {
        get => _enabledCommands;
        set => _enabledCommands = value ?? [];
    }

    /// <summary>Seconds a user has to wait between two commands.</summary>
    public int CooldownSeconds { get; set; } = 3;
    /// <summary>Critical actions allowed per server within <see cref="CriticalWindowMinutes"/>.</summary>
    public int CriticalLimit { get; set; } = DefaultCriticalLimit;
    public int CriticalWindowMinutes { get; set; } = 10;
    /// <summary>Refused commands of one user within <see cref="LockoutMinutes"/> that lock the user out for that time.</summary>
    public int LockoutAttempts { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 10;
    /// <summary>The longest postponement in minutes. The hard limit is <see cref="HardMaxPauseMinutes"/>.</summary>
    public int MaxPauseMinutes { get; set; } = 360;
    /// <summary>Warning time in minutes before a restart or stop started from Discord (0: at once). Players are warned like for a scheduled restart.</summary>
    public int ActionDelayMinutes { get; set; } = 5;
    /// <summary>The minutes offered in the selection when a restart, stop or update is postponed.</summary>
    public List<int> PostponeChoices
    {
        get => _postponeChoices;
        set => _postponeChoices = value ?? [];
    }

    /// <summary>
    /// The server instances the bot shows and controls. Null (not chosen yet): all of them. A list: only these, an empty
    /// list means none. An instance that is not managed has no status message and no button or command can reach it.
    /// </summary>
    public List<int>? ManagedInstances { get; set; }

    /// <summary>Message of the status embed per server instance, so the bot edits it instead of posting a new one.</summary>
    [JsonNumberHandling(IdHandling)]
    public Dictionary<int, ulong> StatusMessageIds
    {
        get => _statusMessageIds;
        set => _statusMessageIds = value ?? [];
    }

    /// <summary>
    /// Which defaults of the limits the file was written with (0: a file of before the limit of critical actions was raised).
    /// Only <see cref="UpgradeLimits"/> reads and sets it.
    /// </summary>
    public int LimitsVersion { get; set; }

    /// <summary>
    /// A file of an older version still holds the old default of critical actions (3), which the bot reached too fast: it
    /// gets the new default once. A value that was chosen on purpose (anything else) stays, and so does a value chosen after
    /// this ran.
    /// </summary>
    public void UpgradeLimits()
    {
        if (LimitsVersion >= LimitsVersionCurrent) return;
        // A changed window shows that the limits were set on purpose: then nothing is touched.
        if (LimitsVersion < 2 && CriticalLimit == OldDefaultCriticalLimit && CriticalWindowMinutes == OldDefaultCriticalWindowMinutes)
            CriticalLimit = DefaultCriticalLimit;
        // The new command to cancel a countdown: whoever may postpone may cancel (it can be switched off like any other).
        if (LimitsVersion < 3 && IsEnabled(BotCommand.Postpone) && !IsEnabled(BotCommand.Cancel)) SetEnabled(BotCommand.Cancel, true);
        LimitsVersion = LimitsVersionCurrent;
    }

    public static string GetPath(GameEdition edition)
    {
        var folder = TrebuchetLib.Services.AppSetup.GetAppConfigDirectory();
        if (!folder.Exists) Directory.CreateDirectory(folder.FullName);
        return Path.Combine(folder.FullName, $"discord-bot.{edition.ToString().ToLowerInvariant()}.json");
    }

    public static string GetTokenPath(GameEdition edition) => Path.ChangeExtension(GetPath(edition), ".token");

    /// <summary>
    /// Loads the settings the way the bot needs them. <see cref="ConfigFile{T}.LoadConfig"/> repairs a damaged file from its
    /// backup, and the backup may still hold users or roles that were removed on purpose: the bot would run with an old list
    /// of who may use it. Here a damaged file is an error and stays as it is. A missing file gives settings that allow nothing.
    /// </summary>
    /// <exception cref="FormatException">The file is not valid JSON.</exception>
    public static DiscordBotConfig LoadStrict(string path)
    {
        if (File.Exists(path) && !IsWellFormedJson(File.ReadAllText(path)))
            throw new FormatException("The file is damaged (no valid JSON). It was left as it is, a backup may exist next to it.");
        var config = LoadConfig(path);
        config.UpgradeLimits();
        return config;
    }

    public bool IsEnabled(BotCommand command) => EnabledCommands.Contains(command.ToString(), StringComparer.Ordinal);

    public void SetEnabled(BotCommand command, bool enabled)
    {
        var name = command.ToString();
        EnabledCommands.RemoveAll(x => string.Equals(x, name, StringComparison.Ordinal));
        if (enabled) EnabledCommands.Add(name);
    }

    /// <summary>The bot answers in this channel only (the command of the channel itself sets it).</summary>
    public void SetAdminChannel(ulong channelId)
    {
        if (!BotId.IsValid(channelId)) throw new ArgumentException("Not a Discord channel ID", nameof(channelId));
        // The status messages sit in the first admin channel. Set again for that very channel they stay: dropped, a second
        // set would be posted next to the old messages, whose buttons still work.
        var sameChannel = AllowedChannelIds.FirstOrDefault() == channelId;
        AllowedChannelIds = [channelId];
        // Otherwise the status messages belong to the old channel.
        if (!sameChannel) StatusMessageIds = [];
    }

    public bool AddUser(ulong id) => AddId(AllowedUserIds, id);
    public bool AddRole(ulong id) => AddId(AllowedRoleIds, id);

    /// <returns>False for the super admin: that user can not be removed.</returns>
    public bool RemoveUser(ulong id) => id != SuperAdminUserId && AllowedUserIds.RemoveAll(x => x == id) > 0;
    public bool RemoveRole(ulong id) => AllowedRoleIds.RemoveAll(x => x == id) > 0;

    private static bool AddId(List<ulong> list, ulong id)
    {
        if (!BotId.IsValid(id) || list.Contains(id)) return false;
        list.Add(id);
        return true;
    }
}
