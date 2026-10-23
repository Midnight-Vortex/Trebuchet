namespace TrebuchetLib.Services;

/// <summary>What <see cref="Launcher.CheckForUpdatesNow"/> found. Plain values, nothing is installed by the check.</summary>
/// <param name="Succeeded">Steam answered for the mods and for the server build.</param>
/// <param name="OutdatedMods">Mods of the server instances (and the client) that have a newer version on the workshop.</param>
/// <param name="ServerUpdate">A newer build of the server is available.</param>
public sealed record LauncherUpdateCheck(bool Succeeded, int OutdatedMods, bool ServerUpdate);
