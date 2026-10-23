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
    /// <summary>Optional channel for critical actions and refused attempts. 0: none.</summary>
    [JsonNumberHandling(IdHandling)]
    public ulong AuditChannelId { get; set; }

    /// <summary>Names of the commands that are switched on. A command missing here is off.</summary>
    public List<string> EnabledCommands
    {
        get => _enabledCommands;
        set => _enabledCommands = value ?? [];
    }

    /// <summary>Seconds a user has to wait between two commands.</summary>
    public int CooldownSeconds { get; set; } = 3;
    /// <summary>Critical actions allowed per server within <see cref="CriticalWindowMinutes"/>.</summary>
    public int CriticalLimit { get; set; } = 3;
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

    /// <summary>Message of the status embed per server instance, so the bot edits it instead of posting a new one.</summary>
    [JsonNumberHandling(IdHandling)]
    public Dictionary<int, ulong> StatusMessageIds
    {
        get => _statusMessageIds;
        set => _statusMessageIds = value ?? [];
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
        return LoadConfig(path);
    }

    private static bool IsWellFormedJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json, new System.Text.Json.JsonDocumentOptions
            {
                CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
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
        AllowedChannelIds = [channelId];
        // The status messages belong to the old channel.
        StatusMessageIds = [];
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
