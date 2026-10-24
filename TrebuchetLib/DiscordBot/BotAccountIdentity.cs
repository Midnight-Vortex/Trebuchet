using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace TrebuchetLib.DiscordBot;

/// <summary>A name or picture of the bot account that can not be taken: the text is safe for a log (no link, no secret).</summary>
public sealed class BotAccountException(string message) : Exception(message);

/// <summary>
/// The bot account itself. The status message with its buttons is written by the bot account, and a bot account has one name
/// and one picture for everything it writes (only a webhook can take another one per message). A fake replaces it in the tests.
/// </summary>
public interface IBotAccountClient
{
    /// <summary>The ID of the bot account, 0 while not connected.</summary>
    ulong UserId { get; }

    /// <summary>The name of the bot account now, null while not connected.</summary>
    string? Username { get; }

    Task SetUsernameAsync(string name, CancellationToken token);

    Task SetAvatarAsync(byte[] image, CancellationToken token);
}

/// <summary>A picture ready to be set: the bytes and what they are (PNG, JPEG, GIF or WebP).</summary>
public sealed record BotAvatarImage(byte[] Bytes, string ContentType);

/// <summary>Fetches the picture a link points to. A fake replaces it in the tests.</summary>
public interface IBotAvatarSource
{
    /// <exception cref="BotAccountException">The picture could not be fetched or is no picture.</exception>
    Task<BotAvatarImage> FetchAsync(string url, CancellationToken token);
}

/// <summary>
/// Remembers which picture the bot account was given (by the hash of its bytes), so a restart of the bot does not upload the same
/// picture again. Not a secret: it sits next to the token.
/// </summary>
public sealed class BotAccountIdentityStore(string path)
{
    private sealed record State(ulong UserId, string AvatarSha256);

    internal string FilePath => path;

    /// <returns>The account and the hash of the picture that was set, (0, "") when none or the file can not be read.</returns>
    public (ulong UserId, string AvatarSha256) Load()
    {
        try
        {
            if (!File.Exists(path)) return (0, string.Empty);
            var state = JsonSerializer.Deserialize<State>(File.ReadAllText(path));
            return state is null ? (0, string.Empty) : (state.UserId, state.AvatarSha256 ?? string.Empty);
        }
        catch (Exception)
        {
            return (0, string.Empty);
        }
    }

    public void Save(ulong userId, string avatarSha256)
    {
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new State(userId, avatarSha256)));
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>
/// Lets the bot account take the name and the picture of the community log: what the status message, the answers and the buttons
/// are written under. Only when the settings ask for it, only what differs from what is there, never in a loop (Discord allows
/// two changes of the name an hour): a change that failed is not tried again for an hour. Never throws and never holds anything up.
/// </summary>
public sealed class BotAccountIdentity
{
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(1);
    private static readonly TimeSpan RecheckAfter = TimeSpan.FromMinutes(10);

    private readonly IBotAccountClient _account;
    private readonly IBotAvatarSource _avatars;
    private readonly BotAccountIdentityStore? _store;
    private readonly Func<DateTime> _clock;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, DateTime> _failed = [];
    private (string Target, DateTime AtUtc)? _checked;

    public BotAccountIdentity(IBotAccountClient account, IBotAvatarSource avatars, BotAccountIdentityStore? store = null,
        Func<DateTime>? clock = null, ILogger? logger = null)
    {
        _account = account;
        _avatars = avatars;
        _store = store;
        _clock = clock ?? (() => DateTime.UtcNow);
        _logger = logger;
    }

    /// <summary>The bot account takes the name and picture of the community log (when the settings ask for it).</summary>
    public async Task ApplyAsync(BotPolicy policy, CancellationToken token)
    {
        if (!policy.BotTakesCommunityIdentity) return;
        var name = policy.CommunityName;
        var url = policy.CommunityAvatarUrl;
        if (name.Length == 0 && url.Length == 0) return;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // Not connected: there is no account to change.
            if (_account.UserId == 0) return;
            var now = _clock();
            // The same settings were looked at a moment ago (a reconnect): once is enough.
            var target = name + "\n" + url + "\n" + _account.UserId;
            if (_checked is { } last && last.Target == target && now - last.AtUtc < RecheckAfter) return;
            _checked = (target, now);
            await ApplyNameAsync(name, now, token).ConfigureAwait(false);
            await ApplyAvatarAsync(url, now, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("Discord bot: the name and picture of the bot could not be set: {Error}", DiscordNetLog.Describe(ex));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ApplyNameAsync(string name, DateTime now, CancellationToken token)
    {
        if (name.Length == 0 || _account.Username is not { } current || string.Equals(current, name, StringComparison.Ordinal)) return;
        if (RecentlyFailed("name:" + name, now)) return;
        try
        {
            await _account.SetUsernameAsync(name, token).ConfigureAwait(false);
            _logger?.LogInformation("Discord bot: the bot now has the name {Name}", name);
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            Failed("name:" + name, now);
            _logger?.LogWarning("Discord bot: the name of the bot could not be changed (Discord allows two changes an hour): {Error}", DiscordNetLog.Describe(ex));
        }
    }

    private async Task ApplyAvatarAsync(string url, DateTime now, CancellationToken token)
    {
        if (url.Length == 0 || RecentlyFailed("avatar:" + url, now)) return;
        try
        {
            var image = await _avatars.FetchAsync(url, token).ConfigureAwait(false);
            var hash = Convert.ToHexString(SHA256.HashData(image.Bytes));
            if (_store?.Load() is var (userId, known) && userId == _account.UserId && known == hash) return;
            await _account.SetAvatarAsync(image.Bytes, token).ConfigureAwait(false);
            _store?.Save(_account.UserId, hash);
            _logger?.LogInformation("Discord bot: the bot now has the picture of the community log");
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            Failed("avatar:" + url, now);
            // The link itself stays out of the message: only what went wrong.
            _logger?.LogWarning("Discord bot: the picture of the bot could not be set: {Error}", DiscordNetLog.Describe(ex));
        }
    }

    private bool RecentlyFailed(string key, DateTime now) => _failed.TryGetValue(key, out var at) && now - at < RetryAfterFailure;

    private void Failed(string key, DateTime now)
    {
        if (_failed.Count > 16) _failed.Clear();
        _failed[key] = now;
    }
}

/// <summary>
/// The picture behind an https link, fetched from this computer: only https on the usual port, no redirects, only addresses on
/// the internet (never this computer or the network at home: the link is typed into the settings, but whoever can change them
/// must not be able to make Trebuchet ask for something inside), at most 4 MB, and it must really be a picture.
/// </summary>
public sealed class HttpBotAvatarSource : IBotAvatarSource, IDisposable
{
    public const int MaxBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _http;

    public HttpBotAvatarSource(HttpMessageHandler? handler = null)
    {
        // The proxy is left out: a proxy could be a computer at home, and the check of the address is made on the way to the server.
        _http = handler is null
            ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, ConnectCallback = ConnectToPublicAddressAsync })
            : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
    }

    public async Task<BotAvatarImage> FetchAsync(string url, CancellationToken token)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443
            || uri.UserInfo.Length > 0 || string.IsNullOrEmpty(uri.Host))
            throw new BotAccountException("The picture link must be an https link without a user part and without a port.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(Timeout);
        try
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new BotAccountException($"The picture could not be fetched (HTTP {(int)response.StatusCode}).");
            if (response.Content.Headers.ContentLength > MaxBytes) throw new BotAccountException("The picture is too big (4 MB at most).");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxBytes) throw new BotAccountException("The picture is too big (4 MB at most).");
                buffer.Write(chunk, 0, read);
            }
            var bytes = buffer.ToArray();
            var type = ImageType(bytes) ?? throw new BotAccountException("The link is no picture (PNG, JPEG, GIF or WebP).");
            return new BotAvatarImage(bytes, type);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new BotAccountException("The picture was not delivered in time.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException)
        {
            throw new BotAccountException("The picture could not be fetched: " + ex.GetType().Name);
        }
    }

    /// <summary>What the first bytes say the picture is, null when it is none of the formats Discord takes.</summary>
    internal static string? ImageType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return "image/png";
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return "image/jpeg";
        if (bytes.Length >= 6 && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8))) return "image/gif";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        return null;
    }

    /// <summary>An address on the internet: not this computer, not the network at home, not reserved.</summary>
    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.None)) return false;
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !(b[0] == 0 || b[0] == 10 || b[0] >= 224
                     || (b[0] == 100 && b[1] is >= 64 and <= 127)
                     || (b[0] == 169 && b[1] == 254)
                     || (b[0] == 172 && b[1] is >= 16 and <= 31)
                     || (b[0] == 192 && b[1] == 168)
                     || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                     || (b[0] == 198 && b[1] is 18 or 19));
        }
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return false;
        // fe80::/10 link local, fc00::/7 unique local, ff00::/8 multicast.
        return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || (b[0] & 0xFE) == 0xFC);
    }

    // The name is looked up here and only an address that passed the check is connected to: no second look-up that could answer
    // something else.
    private static async ValueTask<Stream> ConnectToPublicAddressAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token).ConfigureAwait(false);
        var allowed = addresses.Where(IsPublicAddress).ToArray();
        if (allowed.Length == 0) throw new IOException("The address is not on the internet.");
        Exception? last = null;
        foreach (var address in allowed)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex)
            {
                socket.Dispose();
                last = ex;
            }
        }
        throw last ?? new IOException("No connection.");
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>The real bot account (Discord.Net). Nothing of it is used while there is no connection.</summary>
internal sealed class DiscordNetAccountClient(Func<DiscordSocketClient?> client) : IBotAccountClient
{
    private SocketSelfUser? User => client()?.CurrentUser;

    public ulong UserId => User?.Id ?? 0;

    public string? Username => User?.Username;

    public async Task SetUsernameAsync(string name, CancellationToken token)
    {
        var user = User ?? throw new InvalidOperationException("The bot is not connected.");
        await user.ModifyAsync(x => x.Username = name, new RequestOptions { CancelToken = token }).ConfigureAwait(false);
    }

    public async Task SetAvatarAsync(byte[] image, CancellationToken token)
    {
        var user = User ?? throw new InvalidOperationException("The bot is not connected.");
        // The stream is read while the request is made: it stays open until then.
        using var stream = new MemoryStream(image);
        await user.ModifyAsync(x => x.Avatar = new Image(stream), new RequestOptions { CancelToken = token }).ConfigureAwait(false);
    }
}
