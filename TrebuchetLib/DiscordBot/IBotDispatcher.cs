namespace TrebuchetLib.DiscordBot;

/// <summary>
/// Runs work on the thread the launcher is used on. The tables of the <c>Launcher</c> are plain dictionaries that the
/// 2 second tick of the window reads and writes, so the bot (which lives on Discord's threads) must go through this.
/// The window supplies one that uses the UI thread, the command line host one with a thread of its own
/// (<see cref="SerialBotDispatcher"/>).
/// </summary>
public interface IBotDispatcher
{
    Task<T> InvokeAsync<T>(Func<Task<T>> work);
}
