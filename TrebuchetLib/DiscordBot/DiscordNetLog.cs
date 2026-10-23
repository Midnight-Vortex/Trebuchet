using System.Text;
using Discord;
using Microsoft.Extensions.Logging;

namespace TrebuchetLib.DiscordBot;

/// <summary>
/// Texts of Discord.Net and of exceptions for the log of Trebuchet. Exception objects are never handed to the logger: only
/// their type and message (no data, no stack), each passed through <see cref="BotLogRedactor"/>, so a token in any of
/// them can not reach a log file.
/// </summary>
internal static class DiscordNetLog
{
    private const int MaxDepth = 6;
    private const int MaxLength = 2000;

    public static LogLevel LevelOf(LogSeverity severity) => severity switch
    {
        LogSeverity.Critical => LogLevel.Critical,
        LogSeverity.Error => LogLevel.Error,
        LogSeverity.Warning => LogLevel.Warning,
        LogSeverity.Info => LogLevel.Information,
        LogSeverity.Verbose => LogLevel.Debug,
        _ => LogLevel.Trace
    };

    /// <summary>One line for a message of Discord.Net.</summary>
    public static string Format(LogMessage message)
    {
        var text = $"{message.Source}: {message.Message}";
        if (message.Exception is not null) text += " | " + Describe(message.Exception);
        return Clean(text);
    }

    /// <summary>Type and message of an exception and its inner exceptions.</summary>
    public static string Describe(Exception? exception)
    {
        if (exception is null) return string.Empty;
        var builder = new StringBuilder();
        var queue = new Queue<(Exception ex, int depth)>();
        queue.Enqueue((exception, 0));
        while (queue.Count > 0 && builder.Length < MaxLength)
        {
            var (current, depth) = queue.Dequeue();
            if (builder.Length > 0) builder.Append(" -> ");
            builder.Append(current.GetType().Name).Append(": ").Append(current.Message);
            if (depth >= MaxDepth) continue;
            if (current is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions.Take(MaxDepth)) queue.Enqueue((inner, depth + 1));
            else if (current.InnerException is not null)
                queue.Enqueue((current.InnerException, depth + 1));
        }
        return Clean(builder.ToString());
    }

    // Redact before cutting: a cut through a token could leave a part the pattern no longer finds.
    private static string Clean(string text) => BotDiscordRenderer.Cut(BotLogRedactor.Redact(text).ReplaceLineEndings(" "), MaxLength);
}
