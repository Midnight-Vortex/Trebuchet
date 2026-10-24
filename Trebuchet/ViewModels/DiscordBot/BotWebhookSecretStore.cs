using Trebuchet.ViewModels.SettingFields;
using TrebuchetLib.DiscordBot;

namespace Trebuchet.ViewModels.DiscordBot;

/// <summary>A webhook URL behind a <see cref="SecretField"/> of the panel. It never loads the URL.</summary>
public sealed class BotWebhookSecretStore(BotWebhookStore store) : ISecretStore
{
    public bool HasSecret => store.HasWebhook;

    /// <summary>A webhook never comes from the environment.</summary>
    public bool FromEnvironment => false;

    public bool CanSave => store.CanSave;

    public void Save(string secret) => store.Save(secret);

    public void Delete() => store.Delete();
}
