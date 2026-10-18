using Microsoft.Extensions.Logging;
using TrebuchetLib.Services;

namespace TrebuchetLib.Sequences;

public class SequenceArgs
{
    public CancellationToken CancellationToken { get; set; }
    public string Reason { get; set; } = string.Empty;
    public required Func<Task> MainAction { get; set; }
    public required Launcher Launcher { get; set; }
    public required BackupManager BackupManager { get; set; }
    public required ILogger Logger { get; set; }
    public int Instance { get; set; }

    /// <summary>Expands DSL style macros ({servername}, {reason}, ...) for this instance.</summary>
    public Func<string, string>? MacroExpander { get; set; }

    public string Expand(string text)
    {
        var expanded = MacroExpander?.Invoke(text) ?? text;
        return expanded.Replace("{Reason}", Reason);
    }
}