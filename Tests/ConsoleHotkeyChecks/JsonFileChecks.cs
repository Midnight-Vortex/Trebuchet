using TrebuchetLib;

/// <summary>
/// Settings and profiles: crash safe saves, and damaged files load as backup or defaults instead of throwing.
/// </summary>
static class JsonFileChecks
{
    public static int Run()
    {
        var count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }

        var root = Path.Combine(Path.GetTempPath(), "TrebuchetJsonChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "Config.json");
            var temp = path + JsonFileStorage.TempSuffix;
            var backup = path + JsonFileStorage.BackupSuffix;
            string[] Damaged() => Directory.GetFiles(root, "Config.json" + JsonFileStorage.CorruptSuffix + "*");

            var config = Config.CreateConfig(path);
            config.ServerInstanceCount = 3;
            config.SaveFile();
            Check(File.Exists(path) && !File.Exists(temp), "Save leaves no temporary file");
            Check(Config.LoadConfig(path).ServerInstanceCount == 3, "Saved settings load back");

            // A temporary file left by a crash during an earlier save does not stop the next one.
            File.WriteAllText(temp, "{ half");
            config.ServerInstanceCount = 4;
            config.SaveFile();
            Check(!File.Exists(temp) && Config.LoadConfig(path).ServerInstanceCount == 4, "Save replaces a leftover temporary file");
            Check(File.Exists(backup) && Config.LoadConfig(backup).ServerInstanceCount == 3, "Save keeps the previous version as backup");

            // Cut short by a crash or a full disk: the last good version is used and the damaged file kept.
            var json = File.ReadAllText(path);
            File.WriteAllText(path, json[..(json.Length / 2)]);
            var restored = Config.LoadConfig(path);
            Check(restored.ServerInstanceCount == 3 && restored.FilePath == path, "Damaged settings load from the backup");
            Check(Damaged().Length == 1, "Damaged settings are kept aside for inspection");
            Check(Config.LoadConfig(path).ServerInstanceCount == 3, "The repaired settings file is written back");

            // Empty file without a usable backup: defaults instead of an exception.
            File.Delete(backup);
            File.WriteAllText(path, string.Empty);
            var defaults = Config.LoadConfig(path);
            Check(defaults.ServerInstanceCount == Config.ServerInstanceCountDefault && File.Exists(path), "Empty settings load as defaults");
            File.WriteAllText(path, "null");
            File.WriteAllText(backup, "not json");
            Check(Config.LoadConfig(path).ServerInstanceCount == Config.ServerInstanceCountDefault, "Settings without data and a damaged backup load as defaults");

            // Well formed json that does not fit (e.g. written by a newer version) is no damage: it is not reset to
            // defaults and saved over, the load fails and the file stays as it is.
            var newer = "{ \"ServerInstanceCount\": \"three\" }";
            File.WriteAllText(path, newer);
            var before = Damaged().Length;
            var threw = false;
            try { Config.LoadConfig(path); } catch (Exception) { threw = true; }
            Check(threw && File.ReadAllText(path) == newer && Damaged().Length == before,
                "Valid json of another version is left untouched instead of being reset to defaults");

            // Profiles share the same load path: a damaged one must not crash the profile lists.
            var profilePath = Path.Combine(root, "Profile.json");
            File.WriteAllText(profilePath, "{\"ConsoleHotkey\": \"F1");
            var profile = ProfileFile<ClientProfile>.LoadProfile(profilePath);
            Check(profile.ConsoleHotkey == ClientProfile.ConsoleHotkeyDefault && File.Exists(profilePath), "Damaged profile loads as defaults and stays listed");

            // Deleting a profile removes its backup: a new profile of the same name never loads it.
            profile.SaveFile();
            profile.SaveFile();
            Check(File.Exists(profilePath + JsonFileStorage.BackupSuffix), "Profile save keeps a backup");
            profile.DeleteFile();
            Check(!File.Exists(profilePath + JsonFileStorage.BackupSuffix), "Deleting a profile deletes its backup");
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith("TrebuchetJsonChecks-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected test cleanup path.");
            Directory.Delete(resolved, recursive: true);
        }
        return count;
    }
}
