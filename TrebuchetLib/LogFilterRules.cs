namespace TrebuchetLib;

/// <summary>
/// Rules for the [Core.Log] filters Trebuchet writes into Engine.ini.
/// </summary>
public static class LogFilterRules
{
    /// <summary>
    /// Unreal only knows NoLogging, Fatal, Error, Warning, Display, Log, Verbose and VeryVerbose.
    /// "Critical" (used by older default lists) is not one of them: Unreal ignores it and the category
    /// keeps logging errors. It is written as Fatal, which is what it was meant to do.
    /// </summary>
    public static string NormalizeVerbosity(string verbosity)
    {
        var value = verbosity.Trim();
        return string.Equals(value, "Critical", StringComparison.OrdinalIgnoreCase) ? "Fatal" : value;
    }

    /// <summary>
    /// Filters every server save carries. Without Unreal's voice interface the server asks for it twice per frame
    /// and logs an error each time (LogOnlineSubsystemFls), which floods the log of a server running 24/7.
    /// </summary>
    public static readonly string[] RequiredServerFilters = ["LogOnlineSubsystemFls=Fatal"];

    /// <summary>Adds a required filter whose category is missing. A category the user already set (any level) is kept.</summary>
    /// <returns>true when the list was changed.</returns>
    public static bool EnsureRequiredServerFilters(List<string> filters)
    {
        var changed = false;
        foreach (var required in RequiredServerFilters)
        {
            var category = required[..required.IndexOf('=')];
            if (filters.Any(x => string.Equals(x.Split('=')[0].Trim(), category, StringComparison.OrdinalIgnoreCase))) continue;
            filters.Add(required);
            changed = true;
        }
        return changed;
    }
}
