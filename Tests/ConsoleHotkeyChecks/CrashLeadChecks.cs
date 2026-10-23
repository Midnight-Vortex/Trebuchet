using System.Text.Json;
using TrebuchetLib;

/// <summary>Small crash guards: data from outside (a remote sync profile) must not leave null lists that crash later.</summary>
static class CrashLeadChecks
{
    public static int Run()
    {
        var count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }

        var nulls = JsonSerializer.Deserialize<SyncProfile>("{ \"SyncURL\": \"https://example.invalid/x\", \"Modlist\": null, \"ClientConnections\": null }",
            JsonFile<SyncProfile>._jsonOptions);
        Check(nulls is { Modlist: { Count: 0 }, ClientConnections: { Count: 0 } }, "A remote sync profile with null lists has empty lists, not null");
        var some = JsonSerializer.Deserialize<SyncProfile>("{ \"Modlist\": [\"123\", \"456\"] }", JsonFile<SyncProfile>._jsonOptions);
        Check(some is { Modlist.Count: 2, ClientConnections.Count: 0 }, "Lists that are there stay as they are");
        return count;
    }
}
