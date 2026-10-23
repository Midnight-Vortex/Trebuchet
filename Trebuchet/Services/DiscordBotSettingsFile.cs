using System;
using Microsoft.Extensions.Logging;
using TrebuchetLib;
using TrebuchetLib.DiscordBot;

namespace Trebuchet.Services;

/// <summary>
/// The settings of the Discord bot as Trebuchet loaded them. A file that can not be read must not stop Trebuchet:
/// the bot then gets settings that allow nothing (it does not start), the panel shows <see cref="LoadError"/> and
/// saves nothing, and the unreadable file stays untouched for the user to repair.
/// </summary>
public sealed class DiscordBotSettingsFile
{
    private DiscordBotSettingsFile(DiscordBotConfig config, string path, string tokenPath, string? loadError)
    {
        Config = config;
        Path = path;
        TokenPath = tokenPath;
        LoadError = loadError;
    }

    public DiscordBotConfig Config { get; }
    public string Path { get; }
    public string TokenPath { get; }
    /// <summary>Why the settings could not be read, null when they were.</summary>
    public string? LoadError { get; }
    public bool IsLoaded => LoadError is null;

    public static DiscordBotSettingsFile Load(GameEdition edition, ILogger logger)
    {
        string path;
        string tokenPath;
        try
        {
            path = DiscordBotConfig.GetPath(edition);
            tokenPath = DiscordBotConfig.GetTokenPath(edition);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, @"The folder of the Discord bot settings is not available");
            var name = $@"discord-bot.{edition.ToString().ToLowerInvariant()}";
            var folder = System.IO.Path.GetTempPath();
            // Nothing is read or written there: the bot stays off with the message of the panel.
            return new DiscordBotSettingsFile(DiscordBotConfig.CreateConfig(string.Empty),
                System.IO.Path.Combine(folder, name + @".json"), System.IO.Path.Combine(folder, name + @".token"),
                ex.GetBaseException().Message);
        }
        return Load(path, tokenPath, logger);
    }

    public static DiscordBotSettingsFile Load(string path, string tokenPath, ILogger logger)
    {
        try
        {
            // A damaged file is NOT repaired from its backup here (what JsonFile does for other settings): the backup may
            // still hold users or roles that were removed on purpose. The bot stays off until someone looks at the file.
            return new DiscordBotSettingsFile(DiscordBotConfig.LoadStrict(path), path, tokenPath, null);
        }
        catch (Exception ex)
        {
            // Damaged json is already repaired by LoadConfig (kept as .corrupt). This is a locked file, missing
            // access or a file of a newer version: leave it alone and allow nothing.
            logger.LogError(ex, @"The Discord bot settings {Path} could not be read, the bot stays off", path);
            // Should anything save these defaults, they go next to the real file, never over it.
            var fallback = DiscordBotConfig.CreateConfig(path + @".unreadable");
            return new DiscordBotSettingsFile(fallback, path, tokenPath, ex.GetBaseException().Message);
        }
    }
}
