using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace TrebuchetLib.DiscordBot;

/// <summary>Encrypts and decrypts the token for the file. A different one is used in the tests.</summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] data);
    byte[] Unprotect(byte[] data);
}

/// <summary>
/// Windows data protection for the current user: the file is useless on another Windows account or another computer,
/// and for any program that merely reads the file.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Trebuchet.DiscordBot.Token.v1");

    public byte[] Protect(byte[] data) => ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
}

/// <summary>
/// The bot token. It is not part of the settings, of exports or of backups: it sits protected in its own file, is never
/// shown again after saving and never logged. Without Windows (tests, other systems) the token only comes from the
/// environment variable <see cref="EnvironmentVariable"/>.
/// </summary>
public sealed class BotTokenStore
{
    public const string EnvironmentVariable = "TREBUCHET_DISCORD_TOKEN";

    private readonly string _path;
    private readonly ISecretProtector? _protector;
    private readonly Func<string, string?> _environment;

    public BotTokenStore(string path, ISecretProtector? protector = null, Func<string, string?>? environment = null)
    {
        _path = path;
        _protector = protector ?? (OperatingSystem.IsWindows() ? new DpapiSecretProtector() : null);
        _environment = environment ?? Environment.GetEnvironmentVariable;
    }

    /// <summary>A token is stored in the file, or comes from the environment.</summary>
    public bool HasToken => File.Exists(_path) || FromEnvironment() is not null;

    public bool CanSave => _protector is not null;

    /// <exception cref="ArgumentException">Not a bot token.</exception>
    /// <exception cref="PlatformNotSupportedException">No protection available: use the environment variable.</exception>
    public void Save(string input)
    {
        if (!BotToken.TryNormalize(input, out var token))
            throw new ArgumentException("This is not a Discord bot token.", nameof(input));
        if (_protector is null)
            throw new PlatformNotSupportedException($"The token can not be stored here, set {EnvironmentVariable}.");

        var protectedBytes = _protector.Protect(Encoding.UTF8.GetBytes(token));
        var folder = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        // Temp file and swap: a crash during the write must not leave half a token.
        var temp = _path + ".tmp";
        File.WriteAllBytes(temp, protectedBytes);
        File.Move(temp, _path, overwrite: true);
        BotLogRedactor.RegisterSecret(token);
    }

    /// <returns>The token, or null when none is set or it can not be read (other user, damaged file).</returns>
    public string? Load()
    {
        var token = FromEnvironment();
        if (token is null && _protector is not null && File.Exists(_path))
        {
            try
            {
                var text = Encoding.UTF8.GetString(_protector.Unprotect(File.ReadAllBytes(_path)));
                token = BotToken.TryNormalize(text, out var normalized) ? normalized : null;
            }
            catch (Exception)
            {
                token = null;
            }
        }
        if (token is not null) BotLogRedactor.RegisterSecret(token);
        return token;
    }

    public void Delete()
    {
        if (File.Exists(_path)) File.Delete(_path);
        if (File.Exists(_path + ".tmp")) File.Delete(_path + ".tmp");
    }

    private string? FromEnvironment() => BotToken.TryNormalize(_environment(EnvironmentVariable), out var token) ? token : null;
}

/// <summary>
/// One bot per token. Two Trebuchet windows (e.g. two editions) with the same token would both get every command and
/// both carry it out.
/// </summary>
public sealed class BotTokenLock : IDisposable
{
    private readonly Mutex _mutex;

    private BotTokenLock(Mutex mutex) => _mutex = mutex;

    /// <returns>The lock, or null when another bot already uses this token.</returns>
    public static BotTokenLock? TryAcquire(string token)
    {
        var mutex = new Mutex(false, $"Global\\Trebuchet.DiscordBot.{BotToken.Fingerprint(token)}", out var createdNew);
        if (createdNew) return new BotTokenLock(mutex);
        mutex.Dispose();
        return null;
    }

    public void Dispose() => _mutex.Dispose();
}
