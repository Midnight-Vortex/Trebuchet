using System.Net;
using Microsoft.Extensions.Logging;
using TrebuchetLib.DiscordBot;

/// <summary>
/// The bot account takes the name and picture of the community log: only when asked, only what differs, never in a loop, and the
/// picture is fetched safely (https only, no redirects, only addresses on the internet, a size limit, it must be a picture).
/// </summary>
static class BotAccountIdentityChecks
{
    private const ulong Guild = 100000000000000001;
    private const ulong Operator = 200000000000000002;
    private const string AvatarLink = "https://example.org/logo.png";
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5];
    private static readonly byte[] OtherPng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 9, 9, 9, 9, 9, 9];

    private sealed class FakeAccount : IBotAccountClient
    {
        public ulong UserId { get; set; } = 900000000000000009;
        public string? Username { get; set; } = "Trebuchet Watcher";
        public List<string> Calls { get; } = [];
        public Exception? NameFailure { get; set; }
        public Exception? AvatarFailure { get; set; }

        public Task SetUsernameAsync(string name, CancellationToken token)
        {
            if (NameFailure is not null) return Task.FromException(NameFailure);
            Calls.Add("name " + name);
            Username = name;
            return Task.CompletedTask;
        }

        public Task SetAvatarAsync(byte[] image, CancellationToken token)
        {
            if (AvatarFailure is not null) return Task.FromException(AvatarFailure);
            Calls.Add("avatar " + image.Length);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAvatars : IBotAvatarSource
    {
        public byte[] Bytes { get; set; } = Png;
        public Exception? Failure { get; set; }
        public List<string> Fetched { get; } = [];

        public Task<BotAvatarImage> FetchAsync(string url, CancellationToken token)
        {
            Fetched.Add(url);
            return Failure is not null ? Task.FromException<BotAvatarImage>(Failure) : Task.FromResult(new BotAvatarImage(Bytes, "image/png"));
        }
    }

    private sealed class LogLines : ILogger
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add($"{logLevel}: {formatter(state, exception)}");
    }

    private static DiscordBotConfig Config(Action<DiscordBotConfig>? change = null)
    {
        var config = new DiscordBotConfig { GuildId = Guild, SuperAdminUserId = Operator };
        config.AddRole(300000000000000003);
        config.SetAdminChannel(400000000000000004);
        change?.Invoke(config);
        return config;
    }

    private static BotPolicy PolicyOf(Action<DiscordBotConfig>? change = null)
    {
        var result = BotPolicy.Create(Config(change));
        return result.Policy ?? throw new Exception("settings of the check are invalid: " + string.Join(", ", result.Issues.Select(x => x.Kind + " " + x.Detail)));
    }

    public static async Task<int> Run()
    {
        var count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }
        var folder = Path.Combine(Path.GetTempPath(), "tb-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            SettingChecks(Check, folder);
            await IdentityChecks(Check, folder);
            await SourceChecks(Check);
            AddressChecks(Check);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
        return count;
    }

    private static void SettingChecks(Action<bool, string> check, string folder)
    {
        check(!new DiscordBotConfig().BotTakesCommunityIdentity && !PolicyOf().BotTakesCommunityIdentity, "The bot keeps its own name and picture unless the settings say otherwise");
        check(PolicyOf(c => { c.BotTakesCommunityIdentity = true; c.CommunityName = "Schatten und Licht Server"; }).BotTakesCommunityIdentity, "The setting reaches the policy");
        var path = Path.Combine(folder, "identity.json");
        var saved = DiscordBotConfig.LoadConfig(path);
        saved.BotTakesCommunityIdentity = true;
        saved.CommunityName = "Schatten";
        saved.SaveFile();
        check(DiscordBotConfig.LoadStrict(path) is { BotTakesCommunityIdentity: true, CommunityName: "Schatten" }, "...and is kept in the settings file");

        // A bot account has a shorter name than a webhook message: only when the bot is to take it.
        var tooLong = new string('N', 33);
        foreach (var name in new[] { tooLong, "N" })
        {
            var result = BotPolicy.Create(Config(c => { c.BotTakesCommunityIdentity = true; c.CommunityName = name; }));
            check(!result.IsValid && result.Issues.Any(x => x.Kind == BotConfigIssueKind.BotNameLength && x.Detail == nameof(DiscordBotConfig.CommunityName)),
                $"A name of {name.Length} characters does not fit a bot account");
        }
        check(BotPolicy.Create(Config(c => c.CommunityName = tooLong)).IsValid && BotPolicy.Create(Config(c => c.CommunityName = "N")).IsValid,
            "...but is fine for the webhook alone");
        check(BotPolicy.Create(Config(c => { c.BotTakesCommunityIdentity = true; c.CommunityName = new string('N', 32); })).IsValid
              && BotPolicy.Create(Config(c => { c.BotTakesCommunityIdentity = true; c.CommunityName = "NN"; })).IsValid
              && BotPolicy.Create(Config(c => { c.BotTakesCommunityIdentity = true; c.CommunityName = string.Empty; })).IsValid,
            "2 to 32 characters fit, and no name means only the picture");

        var store = new BotAccountIdentityStore(Path.Combine(folder, "w.identity"));
        check(store.Load() == (0UL, string.Empty), "No picture was set at first");
        store.Save(900000000000000009, "ABCDEF");
        check(store.Load() == (900000000000000009UL, "ABCDEF"), "The picture that was set is remembered");
        File.WriteAllText(store.FilePath, "{ not json");
        check(store.Load() == (0UL, string.Empty), "A file that can not be read is the same as none");
        var both = BotWebhooks.ForTokenPath(Path.Combine(folder, "discord-bot.enhanced.token"));
        check(both.Identity is { } identity && identity.FilePath.EndsWith(".identity", StringComparison.Ordinal) && Path.GetDirectoryName(identity.FilePath) == folder,
            "It sits next to the token");
    }

    private static async Task IdentityChecks(Action<bool, string> check, string folder)
    {
        var now = new DateTime(2026, 10, 24, 15, 0, 0, DateTimeKind.Utc);
        var policy = PolicyOf(c =>
        {
            c.BotTakesCommunityIdentity = true;
            c.CommunityName = "Schatten und Licht Server";
            c.CommunityAvatarUrl = AvatarLink;
        });
        var account = new FakeAccount();
        var avatars = new FakeAvatars();
        var log = new LogLines();
        var store = new BotAccountIdentityStore(Path.Combine(folder, "a.identity"));
        var identity = new BotAccountIdentity(account, avatars, store, () => now, log);

        // Off: nothing is touched, nothing is fetched.
        await identity.ApplyAsync(PolicyOf(c => { c.CommunityName = "Schatten"; c.CommunityAvatarUrl = AvatarLink; }), CancellationToken.None);
        check(account.Calls.Count == 0 && avatars.Fetched.Count == 0, "Without the setting the bot keeps its name and picture");
        await identity.ApplyAsync(PolicyOf(c => c.BotTakesCommunityIdentity = true), CancellationToken.None);
        check(account.Calls.Count == 0 && avatars.Fetched.Count == 0, "Without a name and a picture there is nothing to take");
        account.UserId = 0;
        await identity.ApplyAsync(policy, CancellationToken.None);
        check(account.Calls.Count == 0 && avatars.Fetched.Count == 0, "Not connected: nothing happens");
        account.UserId = 900000000000000009;

        // On: the name and the picture, once.
        await identity.ApplyAsync(policy, CancellationToken.None);
        check(account.Calls.SequenceEqual(["name Schatten und Licht Server", $"avatar {Png.Length}"]) && account.Username == "Schatten und Licht Server",
            "The bot takes the name and picture of the community log: " + string.Join(" | ", account.Calls));
        await identity.ApplyAsync(policy, CancellationToken.None);
        check(account.Calls.Count == 2 && avatars.Fetched.Count == 1, "A second look at once (a reconnect) changes nothing and fetches nothing");
        now = now.AddMinutes(11);
        await identity.ApplyAsync(policy, CancellationToken.None);
        check(account.Calls.Count == 2 && avatars.Fetched.Count == 2, "Later the picture is looked at again, but the same name and picture are not set again");
        var restarted = new BotAccountIdentity(account, avatars, store, () => now, log);
        await restarted.ApplyAsync(policy, CancellationToken.None);
        check(account.Calls.Count == 2, "A restart of the bot does not upload the same picture again");
        avatars.Bytes = OtherPng;
        now = now.AddMinutes(11);
        await identity.ApplyAsync(policy, CancellationToken.None);
        check(account.Calls.Count == 3 && account.Calls[2] == $"avatar {OtherPng.Length}", "A new picture behind the same link is taken");
        account.UserId = 900000000000000010;
        now = now.AddMinutes(11);
        await identity.ApplyAsync(policy, CancellationToken.None);
        check(account.Calls.Count == 4 && account.Calls[3].StartsWith("avatar", StringComparison.Ordinal), "Another bot account gets the picture too");
        account.UserId = 900000000000000009;

        // A name Discord refuses: one warning, no new try for an hour, then again.
        var failing = new FakeAccount { Username = "Old", NameFailure = new InvalidOperationException("You are changing your username too fast " + AvatarLink) };
        var failLog = new LogLines();
        var failClock = now;
        var failingIdentity = new BotAccountIdentity(failing, new FakeAvatars(), null, () => failClock, failLog);
        var nameOnly = PolicyOf(c => { c.BotTakesCommunityIdentity = true; c.CommunityName = "Neuer Name"; });
        await failingIdentity.ApplyAsync(nameOnly, CancellationToken.None);
        failClock = failClock.AddMinutes(11);
        await failingIdentity.ApplyAsync(nameOnly, CancellationToken.None);
        check(failing.Calls.Count == 0 && failLog.Lines.Count(x => x.StartsWith("Warning: Discord bot: the name of the bot could not be changed", StringComparison.Ordinal)) == 1,
            "A name that was refused is told once and not tried again at once: " + string.Join(" | ", failLog.Lines));
        failing.NameFailure = null;
        failClock = failClock.AddMinutes(61);
        await failingIdentity.ApplyAsync(nameOnly, CancellationToken.None);
        check(failing.Calls.SequenceEqual(["name Neuer Name"]), "After an hour it tries again");

        // A picture that can not be fetched: told without the link, the name is taken all the same.
        var noPicture = new FakeAccount { Username = "Old" };
        var noPictureLog = new LogLines();
        var pictureFailure = new BotAccountIdentity(noPicture, new FakeAvatars { Failure = new BotAccountException("The link is no picture (PNG, JPEG, GIF or WebP).") }, null, () => now, noPictureLog);
        await pictureFailure.ApplyAsync(policy, CancellationToken.None);
        check(noPicture.Calls.SequenceEqual(["name Schatten und Licht Server"]) && noPictureLog.Lines.Count(x => x.StartsWith("Warning: Discord bot: the picture of the bot could not be set", StringComparison.Ordinal)) == 1
              && !string.Join('\n', noPictureLog.Lines).Contains("example.org", StringComparison.Ordinal), "A picture that can not be fetched does not stop the name, and the link stays out of the log");
        var refused = new FakeAccount { Username = "Old", AvatarFailure = new InvalidOperationException("Invalid image") };
        var refusedLog = new LogLines();
        await new BotAccountIdentity(refused, new FakeAvatars(), null, () => now, refusedLog).ApplyAsync(policy, CancellationToken.None);
        check(refusedLog.Lines.Count(x => x.StartsWith("Warning:", StringComparison.Ordinal)) == 1 && refused.Calls.Count == 1, "A picture that Discord refuses is one warning");

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var cancelAccount = new FakeAccount { Username = "Old" };
        try
        {
            await new BotAccountIdentity(cancelAccount, new FakeAvatars(), null, () => now, null).ApplyAsync(policy, cancelled.Token);
            check(false, "A cancelled call is cancelled");
        }
        catch (OperationCanceledException)
        {
            check(cancelAccount.Calls.Count == 0, "A cancelled call does nothing");
        }
    }

    private sealed class FakeHttp : HttpMessageHandler
    {
        public int Requests;
        public Func<HttpRequestMessage, HttpResponseMessage> Answer { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) };
        public Exception? Failure { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            if (Failure is not null) return Task.FromException<HttpResponseMessage>(Failure);
            return Task.FromResult(Answer(request));
        }
    }

    private sealed class NonSeekable(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task<Exception?> Capture(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static async Task SourceChecks(Action<bool, string> check)
    {
        var http = new FakeHttp();
        using var source = new HttpBotAvatarSource(http);
        var png = await source.FetchAsync(AvatarLink, CancellationToken.None);
        check(png.ContentType == "image/png" && png.Bytes.SequenceEqual(Png) && http.Requests == 1, "A picture behind an https link is fetched");
        foreach (var (bytes, type) in new (byte[], string)[]
                 {
                     ([0xFF, 0xD8, 0xFF, 0xE0, 0, 0], "image/jpeg"), ("GIF89a......"u8.ToArray(), "image/gif"), ("GIF87a......"u8.ToArray(), "image/gif"),
                     ("RIFF\0\0\0\0WEBPVP8 "u8.ToArray(), "image/webp")
                 })
        {
            http.Answer = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            check((await source.FetchAsync(AvatarLink, CancellationToken.None)).ContentType == type, "The format is told from the bytes: " + type);
        }

        // Links that are not taken, before anything is asked.
        var before = http.Requests;
        foreach (var bad in new[] { "http://example.org/a.png", "https://example.org:8443/a.png", "https://user:pw@example.org/a.png", "ftp://example.org/a.png", "example.org/a.png", "file:///etc/passwd", "" })
            check(await Capture(() => source.FetchAsync(bad, CancellationToken.None)) is BotAccountException, "Not a picture link: " + bad);
        check(http.Requests == before, "...and nothing was asked for them");

        // What the server answers.
        foreach (var (status, why) in new[] { (HttpStatusCode.Found, "a redirect"), (HttpStatusCode.NotFound, "not found"), (HttpStatusCode.Forbidden, "forbidden") })
        {
            http.Answer = _ => new HttpResponseMessage(status);
            check(await Capture(() => source.FetchAsync(AvatarLink, CancellationToken.None)) is BotAccountException { Message: var m } && m.Contains(((int)status).ToString()), "Refused: " + why);
        }
        http.Answer = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("<html>not a picture</html>"u8.ToArray()) };
        check(await Capture(() => source.FetchAsync(AvatarLink, CancellationToken.None)) is BotAccountException { Message: var notPicture } && notPicture.Contains("no picture"),
            "A page is no picture");
        var big = new byte[HttpBotAvatarSource.MaxBytes + 1];
        Png.CopyTo(big, 0);
        http.Answer = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(big) };
        check(await Capture(() => source.FetchAsync(AvatarLink, CancellationToken.None)) is BotAccountException { Message: var tooBig } && tooBig.Contains("too big"),
            "A picture over 4 MB is refused by its announced size");
        http.Answer = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NonSeekable(new MemoryStream(big))) };
        check(await Capture(() => source.FetchAsync(AvatarLink, CancellationToken.None)) is BotAccountException { Message: var tooBigStream } && tooBigStream.Contains("too big"),
            "...and when it announces nothing, by what arrives");
        http.Failure = new HttpRequestException("connection to " + AvatarLink + " failed");
        check(await Capture(() => source.FetchAsync(AvatarLink, CancellationToken.None)) is BotAccountException { Message: var down } && !down.Contains("example.org"),
            "A lost connection is an error without the link");
        http.Failure = null;
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        check(await Capture(() => source.FetchAsync(AvatarLink, cancel.Token)) is OperationCanceledException, "A cancelled call is cancelled");
    }

    private static void AddressChecks(Action<bool, string> check)
    {
        foreach (var address in new[]
                 {
                     "127.0.0.1", "127.5.5.5", "::1", "10.0.0.5", "172.16.0.1", "172.31.255.255", "192.168.1.1", "169.254.1.1", "100.64.0.1", "100.127.255.255", "0.0.0.0", "0.1.2.3",
                     "224.0.0.1", "239.255.255.255", "255.255.255.255", "198.18.0.1", "198.19.0.1", "192.0.0.1", "::ffff:10.0.0.1", "::ffff:127.0.0.1", "fe80::1", "fc00::1", "fd12:3456::1",
                     "ff02::1", "::"
                 })
            check(!HttpBotAvatarSource.IsPublicAddress(IPAddress.Parse(address)), "Not an address on the internet: " + address);
        foreach (var address in new[] { "8.8.8.8", "1.1.1.1", "172.15.0.1", "172.32.0.1", "192.169.0.1", "100.63.255.255", "100.128.0.1", "198.17.0.1", "198.20.0.1", "2606:4700:4700::1111", "::ffff:8.8.8.8" })
            check(HttpBotAvatarSource.IsPublicAddress(IPAddress.Parse(address)), "An address on the internet: " + address);
        check(HttpBotAvatarSource.ImageType([]) is null && HttpBotAvatarSource.ImageType([0x89, 0x50]) is null && HttpBotAvatarSource.ImageType("RIFF\0\0\0\0WAVE"u8) is null
              && HttpBotAvatarSource.ImageType("GIF90a"u8) is null, "Short or wrong bytes are no picture");
    }
}
