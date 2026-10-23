using System;
using Trebuchet.ViewModels.SettingFields;
using TrebuchetLib.DiscordBot;

namespace Trebuchet.ViewModels.DiscordBot;

/// <summary>The bot token behind the <see cref="SecretField"/> of the panel. It never loads the token.</summary>
public sealed class BotTokenSecretStore(BotTokenStore store, Func<string, string?>? environment = null) : ISecretStore
{
    private readonly Func<string, string?> _environment = environment ?? Environment.GetEnvironmentVariable;

    public bool HasSecret => store.HasToken;

    /// <summary>Same rule as <see cref="BotTokenStore"/>: a valid token in the variable wins over the file.</summary>
    public bool FromEnvironment => BotToken.TryNormalize(_environment(BotTokenStore.EnvironmentVariable), out _);

    public bool CanSave => store.CanSave;

    public void Save(string secret) => store.Save(secret);

    public void Delete() => store.Delete();
}
