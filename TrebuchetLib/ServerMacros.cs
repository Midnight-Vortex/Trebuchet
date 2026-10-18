using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using TrebuchetLib.Processes;

namespace TrebuchetLib;

/// <summary>
/// Values available to message macros (Discord notifications, RCon commands, sequence messages).
/// </summary>
public sealed class MacroContext
{
    public ServerProfile? Profile { get; init; }
    public IConanServerProcess? Process { get; init; }
    public int Instance { get; init; }
    public string Reason { get; init; } = string.Empty;
    public string ExternalIp { get; init; } = string.Empty;
    public string EngineVersion { get; init; } = string.Empty;
    public string BranchName { get; init; } = string.Empty;
    public string ServerRevision { get; init; } = string.Empty;
    public string MapName { get; init; } = string.Empty;
    public Dictionary<string, string> Extra { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Expands the "magic tokens" known from the Funcom Dedicated Server Launcher, e.g. {servername}, {reason}, {ip},
/// {game_port}. Tokens are case-insensitive; unknown tokens are left untouched.
/// </summary>
public static partial class ServerMacros
{
    public static readonly IReadOnlyList<string> Tokens =
    [
        "{servername}", "{reason}", "{ip}", "{game_port}",
        "{steam_port}", "{rcon_port}", "{server_password}", "{admin_password}", "{rcon_password}",
        "{trebuchet_version}", "{dsl_version}", "{server_revision}", "{branch_name}", "{server_pid}", "{map_name}",
        "{date_now_iso}", "{date_now_human}", "{engine_version}", "{players}", "{max_players}", "{instance}"
    ];

    public static string Expand(string template, MacroContext context)
    {
        if (string.IsNullOrEmpty(template) || !template.Contains('{')) return template;
        var values = BuildValues(context);
        return TokenRegex().Replace(template, match =>
            values.TryGetValue(match.Groups[1].Value, out var value) ? value : match.Value);
    }

    private static string GetConnectAddress(MacroContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.ExternalIp)) return context.ExternalIp;
        if (context.Profile is { EnableMultiHome: true } p && !string.IsNullOrWhiteSpace(p.MultiHomeAddress))
            return p.MultiHomeAddress.Trim();
        return "127.0.0.1";
    }

    private static Dictionary<string, string> BuildValues(MacroContext context)
    {
        var profile = context.Profile;
        var process = context.Process;
        var serverName = profile?.ServerName ?? process?.Infos.Title ?? string.Empty;
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? string.Empty;
        var now = DateTime.Now;

        var gamePort = (profile?.GameClientPort ?? process?.Infos.Port ?? ServerProfile.GameClientPortDefault)
            .ToString(CultureInfo.InvariantCulture);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["servername"] = serverName,
            // steam://connect links were removed. Texts saved with the old tokens get the plain address instead of
            // showing the raw token.
            ["steamconnect"] = GetConnectAddress(context) + ":" + gamePort,
            ["steamconnect_nopassword"] = GetConnectAddress(context) + ":" + gamePort,
            ["reason"] = context.Reason,
            ["ip"] = GetConnectAddress(context),
            ["game_port"] = gamePort,
            ["steam_port"] = (profile?.SourceQueryPort ?? process?.Infos.QueryPort ?? ServerProfile.SourceQueryPortDefault)
                .ToString(CultureInfo.InvariantCulture),
            ["rcon_port"] = (profile?.RConPort ?? process?.Infos.RConPort ?? ServerProfile.RConPortDefault)
                .ToString(CultureInfo.InvariantCulture),
            ["server_password"] = profile?.ServerPassword ?? string.Empty,
            ["admin_password"] = profile?.AdminPassword ?? string.Empty,
            ["rcon_password"] = profile?.RConPassword ?? process?.Infos.RConPassword ?? string.Empty,
            ["trebuchet_version"] = version,
            ["dsl_version"] = version,
            ["server_revision"] = context.ServerRevision,
            ["server_snapshot"] = context.ServerRevision,
            ["branch_name"] = context.BranchName,
            ["server_pid"] = process?.PId.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            ["map_name"] = string.IsNullOrEmpty(context.MapName) ? GetMapName(profile?.Map) : context.MapName,
            ["date_now_iso"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            ["date_now_human"] = now.ToString("f", CultureInfo.CurrentCulture),
            ["engine_version"] = context.EngineVersion,
            ["players"] = process?.Players.ToString(CultureInfo.InvariantCulture) ?? "0",
            ["max_players"] = (process?.MaxPlayers is > 0 ? process.MaxPlayers : profile?.MaxPlayers ?? 0)
                .ToString(CultureInfo.InvariantCulture),
            ["instance"] = context.Instance.ToString(CultureInfo.InvariantCulture),
        };

        foreach (var pair in context.Extra)
            values[pair.Key] = pair.Value;
        return values;
    }

    private static string GetMapName(string? mapPath)
    {
        if (string.IsNullOrWhiteSpace(mapPath)) return string.Empty;
        var name = mapPath.Trim().TrimEnd('/');
        var index = name.LastIndexOf('/');
        return index >= 0 ? name[(index + 1)..] : name;
    }

    [GeneratedRegex(@"\{([A-Za-z_]+)\}")]
    private static partial Regex TokenRegex();
}
