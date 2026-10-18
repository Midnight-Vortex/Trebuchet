namespace TrebuchetLib;

/// <summary>
/// DSL "Warning Messages": a message sent to the players this long before a restart.
/// </summary>
public class RestartWarning
{
    public RestartWarning()
    {
    }

    public RestartWarning(TimeSpan before, string message)
    {
        Before = before;
        Message = message;
    }

    public TimeSpan Before { get; set; } = TimeSpan.FromMinutes(5);
    public string Message { get; set; } = string.Empty;
}
