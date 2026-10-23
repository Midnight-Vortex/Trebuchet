namespace TrebuchetLib.DiscordBot;

/// <summary>
/// Runs work on the thread the launcher is used on. The tables of the <c>Launcher</c> are plain dictionaries that the
/// 2 second tick of the window reads and writes, so the bot (which lives on Discord's threads) must go through this.
/// The window supplies one that uses the UI thread, the command line host (no UI thread) runs the work directly.
/// </summary>
public interface IBotDispatcher
{
    Task<T> InvokeAsync<T>(Func<Task<T>> work);
}

/// <summary>For hosts without a tick: the launcher is only used through the bot.</summary>
public sealed class DirectBotDispatcher : IBotDispatcher
{
    public Task<T> InvokeAsync<T>(Func<Task<T>> work) => work();
}
