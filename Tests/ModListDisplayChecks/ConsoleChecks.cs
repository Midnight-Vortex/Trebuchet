using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AvaloniaEdit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Trebuchet;
using Trebuchet.ViewModels;
using TrebuchetLib;
using TrebuchetLib.Processes;
using TrebuchetLib.Services;

static class ConsoleChecks
{
    public static int RunUi()
    {
        var count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }
        var config = new UIConfig();
        Check(config.GetInstanceFilter(0, ConsoleLogSource.ServerLog), "Fresh consoles show server logs");
        config.SetInstancePopup(2, true);
        Check(config.GetInstanceFilter(0, ConsoleLogSource.Trebuchet) && config.GetInstanceFilter(2, ConsoleLogSource.ServerLog), "Undocking preserves default filters for all instances");
        config.SetInstanceFilter(0, ConsoleLogSource.ServerLog, false);
        config.SetInstancePopup(3, true);
        Check(!config.GetInstanceFilter(0, ConsoleLogSource.ServerLog), "Explicitly disabled filters stay disabled");

        var first = new TextSource();
        first.Append("existing\n");
        var editor = new TextEditor();
        var behavior = new ConsoleTextBindingBehavior { TextSource = first };
        behavior.Attach(editor);
        Check(editor.Text == first.Text, "Source set before attachment must show existing logs");
        first.Append("live\n");
        Check(editor.Text == first.Text, "Attached console receives live logs");
        var second = new TextSource();
        second.Append("other\n");
        behavior.TextSource = second;
        first.Clear();
        first.Append("stale\n");
        Check(editor.Text == second.Text, "Switching console detaches both events from old source");
        second.Append(new string('x', 200));
        Check(editor.Text.Length == second.MaxChar && editor.Text == second.Text, "Long lines trim safely to the console memory limit");
        behavior.Detach();
        second.Append("after detach\n");
        behavior.Attach(editor);
        Check(editor.Text == second.Text, "Reattached console restores logs written while detached");
        behavior.Detach();

        using var sink = new InternalLogSink();
        var console = new MixedConsoleViewModel(new UIConfig(), 0, sink, NullLogger.Instance);
        var server = new TestServer();
        console.Process = server;
        Check(console.ServerLabel.Contains("Test Enhanced Server") && !console.CanSend, "Running server is named even with RCON disabled");
        using var rcon = new Rcon(new IPEndPoint(IPAddress.Loopback, 25575), "test", NullLogger<Rcon>.Instance);
        server.RCon = rcon;
        console.RefreshLabel();
        Check(console.CanSend && console.ServerLabel.Contains("25575"), "RCON works while process is running before query service becomes online");
        server.SetState(ProcessState.ONLINE);
        Check(console.CanSend && console.ServerLabel.Contains(Trebuchet.Assets.Resources.ConsoleOnline), "Online status refreshes the selector");
        console.Process = null;
        server.SetState(ProcessState.RUNNING);
        Check(!console.CanSend && !console.ServerLabel.Contains("Test Enhanced Server"), "Stopped or detached server clears name and command availability");

        var setup = new AppSetup(new Config { DataDirectory = Path.GetTempPath(), ServerInstanceCount = 1 }, GameEdition.Enhanced, false, false);
        Check(setup.TryGetInstanceIndexFromPath(setup.GetInstanceInternalBinary(0).ToUpperInvariant(), out var instance) && instance == 0, "Windows process paths match regardless of casing");
        return count;
    }

    public static async Task<int> RunIo()
    {
        var count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); count++; }
        var folder = Path.Combine(Path.GetTempPath(), "Trebuchet-console-checks-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "server.log");
            await File.WriteAllTextAsync(path, "existing old log\n", new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-1));
            var logger = new CaptureLogger();
            using (var reader = new LogReader(logger, path))
            {
                reader.Start();
                await Until(() => logger.Lines.Contains("existing old log"));
                Check(true, "Attaching reads existing recent log content even when file is old");
                await File.AppendAllTextAsync(path, "Grüße 😀\n", new UTF8Encoding(false));
                await Until(() => logger.Lines.Contains("Grüße 😀"));
                await Task.Delay(650);
                Check(logger.Lines.Count(x => x == "Grüße 😀") == 1 && logger.Lines.Count == 2, "UTF-8 byte offsets never duplicate or corrupt Unicode logs");
                var split = Encoding.UTF8.GetBytes("split €\n");
                await Append(path, split[..7]);
                await Task.Delay(650);
                Check(logger.Lines.Count == 2, "Incomplete UTF-8 and log lines wait for completion");
                await Append(path, split[7..]);
                await Until(() => logger.Lines.Contains("split €"));
                Check(true, "UTF-8 decoder resumes across polling reads");
                await File.WriteAllTextAsync(path, "rotated\n", new UTF8Encoding(false));
                await Until(() => logger.Lines.Contains("rotated"));
                Check(true, "Truncated log resumes from start");
            }

            // Keep the local port reserved without listening: no real game/server is contacted.
            using var reserved = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            reserved.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            using var rcon = new Rcon((IPEndPoint)reserved.LocalEndPoint!, "test", NullLogger<Rcon>.Instance);
            for (var i = 0; i < 2; i++)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                try { await rcon.Send("test", timeout.Token); throw new Exception("Expected connection refusal"); }
                catch (SocketException) { Check(true, "Failed RCON connection releases lock for retry"); }
            }
            rcon.QueueData("test");
            for (var i = 0; i < 2; i++)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                try { await rcon.FlushQueue(timeout.Token); throw new Exception("Expected connection refusal"); }
                catch (SocketException) { Check(true, "Failed queued RCON connection releases lock and retains pending commands"); }
            }

            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var token = testTimeout.Token;
            var serverTask = Task.Run(async () =>
            {
                using (var rejected = await listener.AcceptTcpClientAsync(token))
                {
                    var stream = rejected.GetStream();
                    await ReadPacket(stream, token);
                    await WritePacket(stream, -1, 2, "", token);
                }
                using var accepted = await listener.AcceptTcpClientAsync(token);
                var acceptedStream = accepted.GetStream();
                var auth = await ReadPacket(acceptedStream, token);
                await WritePacket(acceptedStream, auth.id, 2, "", token);
                var command = await ReadPacket(acceptedStream, token);
                if (command.body != "ListPlayers") throw new Exception("Wrong RCON command payload");
                await WritePacket(acceptedStream, command.id, 0, "Test player", token);
            }, token);
            using var connectedRcon = new Rcon((IPEndPoint)listener.LocalEndpoint, "test", NullLogger<Rcon>.Instance) { Timeout = 2 };
            var rejectedAuth = false;
            try { await connectedRcon.Send("ListPlayers", token); }
            catch (Exception ex) when (ex.Message == "Authentication failed.") { rejectedAuth = true; }
            Check(rejectedAuth, "Authentication failure must be reported instead of sending commands unauthenticated");
            var reply = await connectedRcon.Send("ListPlayers", token);
            Check(reply.Exception is null && reply.Response == "Test player", "Retry reconnects, authenticates and receives command output after failed authentication");
            await serverTask;
        }
        finally { Directory.Delete(folder, true); }
        return count;
    }

    private static async Task Append(string path, byte[] bytes)
    {
        await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        await stream.WriteAsync(bytes);
    }

    private static async Task<(int id, string body)> ReadPacket(NetworkStream stream, CancellationToken token)
    {
        var prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, token);
        var packet = new byte[BitConverter.ToInt32(prefix)];
        await stream.ReadExactlyAsync(packet, token);
        return (BitConverter.ToInt32(packet), Encoding.ASCII.GetString(packet, 8, packet.Length - 10));
    }

    private static async Task WritePacket(NetworkStream stream, int id, int type, string body, CancellationToken token)
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        writer.Write(body.Length + 10);
        writer.Write(id);
        writer.Write(type);
        writer.Write(Encoding.ASCII.GetBytes(body));
        writer.Write((short)0);
        await stream.WriteAsync(bytes.ToArray(), token);
    }

    private static async Task Until(Func<bool> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed > TimeSpan.FromSeconds(5)) throw new Exception("Timed out waiting for server log output");
            await Task.Delay(30);
        }
    }

    private sealed class CaptureLogger : ILogger<LogReader>
    {
        public ConcurrentQueue<string> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Lines.Enqueue(formatter(state, exception));
    }

    private sealed class TextSource : ITextSource
    {
        public string Text { get; private set; } = "";
        public bool AutoScroll => false;
        public int MaxChar => 100;
        public event EventHandler<string>? TextAppended;
        public event EventHandler? TextCleared;
        public void Append(string text) { Text += text; if (Text.Length > MaxChar) Text = Text[^MaxChar..]; TextAppended?.Invoke(this, text); }
        public void Clear() { Text = ""; TextCleared?.Invoke(this, EventArgs.Empty); }
    }

    private sealed class TestServer : IConanServerProcess
    {
        public ConanServerInfos Infos { get; } = new() { Title = "Test Enhanced Server", RConPort = 25575 };
        public IRcon? RCon { get; set; }
        public ProcessState State { get; private set; } = ProcessState.RUNNING;
        public event EventHandler<ProcessState>? StateChanged;
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public void SetState(ProcessState state) { State = state; StateChanged?.Invoke(this, state); }
        public Process Process => throw new NotSupportedException();
        public int PId => 0;
        public long MemoryUsage => 0;
        public TimeSpan CpuTime => TimeSpan.Zero;
        public DateTime StartUtc => DateTime.UtcNow;
        public int MaxPlayers => 40;
        public int Players => 0;
        public bool Online => State == ProcessState.ONLINE;
        public bool RequestRestart => false;
        public bool KillZombies { get; set; }
        public int ZombieCheckSeconds { get; set; }
        public void Dispose() { }
        public Task RefreshAsync() => Task.CompletedTask;
        public Task KillAsync() => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public Task RestartAsync() => Task.CompletedTask;
    }
}
