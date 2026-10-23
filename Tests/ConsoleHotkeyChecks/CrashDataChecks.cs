using System.Reflection;
using System.Runtime.CompilerServices;
using DepotDownloader;
using SteamKit2;
using TrebuchetLib;
using TrebuchetLib.Services;
using TrebuchetLib.YuuIni;

/// <summary>
/// Crash and hang causes in the Steam, ini and file layers: failures that ended the process or stopped servers from
/// being managed.
/// </summary>
static class CrashDataChecks
{
    public static async Task<int> Run()
    {
        var count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }

        ConnectedHandlerNeverThrows(Check);
        await RunningServerInfosReadEmptySections(Check);
        await IniWritesReplaceTheFile(Check);
        return count;
    }

    /// <summary>
    /// The connect handler of the Steam session is async void: an exception (e.g. LogOnAnonymous rethrowing a failed
    /// machine id lookup) went to the thread pool and ended Trebuchet. It must end the logon attempt instead.
    /// </summary>
    private static void ConnectedHandlerNeverThrows(Action<bool, string> check)
    {
        var session = new Steam3Session(new SteamUser.LogOnDetails());
        var context = new RecordingContext();
        var previous = SynchronizationContext.Current;
        try
        {
            // Makes the logon call throw like a failing SteamKit would.
            session.steamUser = null!;
            var handler = typeof(Steam3Session).GetMethod("ConnectedCallback", BindingFlags.Instance | BindingFlags.NonPublic)
                          ?? throw new Exception("Steam3Session.ConnectedCallback not found");
            var connected = RuntimeHelpers.GetUninitializedObject(typeof(SteamClient.ConnectedCallback));
            // An async void method reports its exception to the context it started on: recorded instead of a crash.
            SynchronizationContext.SetSynchronizationContext(context);
            handler.Invoke(session, [connected]);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        check(context.Errors.Count == 0,
            "A failing Steam logon must not escape the async void connect handler (it ends the process): "
            + string.Join("; ", context.Errors.Select(e => e.GetType().Name + ": " + e.Message)));
        var aborted = (bool)(typeof(Steam3Session).GetField("bAborted", BindingFlags.Instance | BindingFlags.NonPublic)
                             ?.GetValue(session) ?? false);
        check(aborted && !session.IsConnecting && !session.IsLoggedOn,
            "A failed logon ends the connection attempt, so Connect() returns instead of waiting for its timeout");
        session.Dispose();
    }

    /// <summary>
    /// Re-attaching a running server after a restart of Trebuchet read its ini files with the default parser, which
    /// throws on the empty section names Funcom writes ("[]"): the server was never attached again.
    /// </summary>
    private static async Task RunningServerInfosReadEmptySections(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "TrebuchetCrashDataChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var setup = new AppSetup(new Config { DataDirectory = root, ServerInstanceCount = 1 }, GameEdition.Enhanced, false, false);
            async Task WriteIni(string name, string content)
            {
                var path = Path.Combine(setup.GetInstancePath(0), string.Format(Constants.FileIniServer, name));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, content);
            }

            await WriteIni("Engine", "[]\nStray=1\n[URL]\nPort=7790\n[OnlineSubsystem]\nServerName=Crash Check\n"
                                     + "[OnlineSubsystemSteam]\nGameServerQueryPort=27100\n");
            await WriteIni("Game", "[RconPlugin]\nRconEnabled=True\nRconPort=25600\nRconPassword=secret\n[]\nStray=2\n");

            TrebuchetLib.Processes.ConanServerInfos infos;
            try
            {
                infos = await setup.GetInfosFromIni(0);
            }
            catch (Exception ex)
            {
                throw new Exception("Ini files with an empty section name must not stop a running server from being attached", ex);
            }
            check(infos.Port == 7790 && infos.QueryPort == 27100 && infos.Title == "Crash Check",
                "Engine.ini values are read next to an empty section");
            check(infos.RConPort == 25600 && infos.RConPassword == "secret", "Game.ini values are read next to an empty section");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* best effort */ }
        }
    }

    /// <summary>Ini files are written next to the target, flushed and swapped in: no temporary file stays.</summary>
    private static async Task IniWritesReplaceTheFile(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "TrebuchetCrashDataChecks-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(root, "Config", "ServerSettings.ini");
            await Tools.SetFileContent(path, "[ServerSettings]\nAdminPassword=a\n");
            await Tools.SetFileContent(path, "[ServerSettings]\nAdminPassword=b\n");
            check(await File.ReadAllTextAsync(path) == "[ServerSettings]\nAdminPassword=b\n" && !File.Exists(path + ".tmp"),
                "An ini save replaces the file and leaves no temporary file");
            var bytes = await File.ReadAllBytesAsync(path);
            check(bytes.Length > 0 && bytes[0] == (byte)'[', "Ini files are written without a byte order mark, as before");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* best effort */ }
        }
    }

    private sealed class RecordingContext : SynchronizationContext
    {
        public List<Exception> Errors { get; } = [];

        public override void Post(SendOrPostCallback d, object? state)
        {
            try { d(state); }
            catch (Exception ex) { lock (Errors) Errors.Add(ex); }
        }

        public override void Send(SendOrPostCallback d, object? state) => Post(d, state);
    }
}
