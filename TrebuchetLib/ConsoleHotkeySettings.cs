using System.Text;
using System.Text.RegularExpressions;

namespace TrebuchetLib;

/// <summary>Updates Input.ini without reserializing mappings, comments or console history.</summary>
public static class ConsoleHotkeySettings
{
    public static IReadOnlyList<string> SupportedKeys { get; } = Array.AsReadOnly<string>(
        ["Insert", "Home", "End", "PageUp", "PageDown", "Pause", "ScrollLock", "Tilde",
         "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12"]);

    public static async Task EnsureFile(string path, string hotkey)
    {
        var content = string.Empty;
        Encoding encoding = new UTF8Encoding(false, true);
        if (File.Exists(path))
        {
            using var reader = new StreamReader(path, encoding, detectEncodingFromByteOrderMarks: true);
            content = await reader.ReadToEndAsync();
            encoding = reader.CurrentEncoding;
        }

        var updated = EnsureBinding(content, hotkey);
        if (updated == content) return;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, updated, encoding);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public static string EnsureBinding(string content, string hotkey)
    {
        var key = SupportedKeys.FirstOrDefault(x => string.Equals(x, hotkey, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("Unsupported console hotkey.", nameof(hotkey));
        const string section = "[/Script/Engine.InputSettings]";
        var inSection = false;
        var foundSection = false;
        var hasKey = false;
        var insertionIndex = content.Length;
        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        foreach (Match line in Regex.Matches(content, @"^.*$", RegexOptions.Multiline))
        {
            var text = line.Value.Trim();
            if (text.StartsWith(';') || text.Length == 0) continue;
            if (text.StartsWith('['))
            {
                if (inSection) insertionIndex = line.Index;
                inSection = string.Equals(text, section, StringComparison.OrdinalIgnoreCase);
                if (inSection)
                {
                    foundSection = true;
                    insertionIndex = content.Length;
                }
                continue;
            }
            if (!inSection) continue;
            var equals = text.IndexOf('=');
            if (equals < 0) continue;
            var name = text[..equals].Trim();
            var value = text[(equals + 1)..].Trim().Trim('"');
            var matchesKey = string.Equals(value, key, StringComparison.OrdinalIgnoreCase);
            if (name.Equals("!ConsoleKeys", StringComparison.OrdinalIgnoreCase)) hasKey = false;
            else if (name.Equals("-ConsoleKeys", StringComparison.OrdinalIgnoreCase) && matchesKey) hasKey = false;
            else if (name.Equals("ConsoleKeys", StringComparison.OrdinalIgnoreCase)) hasKey = matchesKey;
            else if ((name.Equals("+ConsoleKeys", StringComparison.OrdinalIgnoreCase)
                      || name.Equals(".ConsoleKeys", StringComparison.OrdinalIgnoreCase)) && matchesKey) hasKey = true;
        }

        if (hasKey) return content;
        var prefix = insertionIndex > 0 && content[insertionIndex - 1] != '\n' ? newline : string.Empty;
        var addition = prefix + (foundSection ? string.Empty : section + newline) + "+ConsoleKeys=" + key + newline;
        return content.Insert(insertionIndex, addition);
    }
}
