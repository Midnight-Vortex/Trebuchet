using System.Text.RegularExpressions;
using System.Text;
using Microsoft.Extensions.Logging;

namespace TrebuchetLib;

public partial class LogReader(ILogger<LogReader> logger, string logPath) : IDisposable
{
    private readonly Dictionary<string, object> _loggerContext = new()
    {
        {"TrebSource", ConsoleLogSource.ServerLog},
        {"file", logPath}
    };
    private long _offset = -1;
    private CancellationTokenSource? _cts;
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private string _pendingLine = string.Empty;
    private bool _skipPartialLine;

    public string LogPath { get; init; } = logPath;

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }

    public LogReader SetContext(string key, object value)
    {
        _loggerContext[key] = value;
        return this;
    }

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new();
        var token = _cts.Token;
        Task.Run(() => BackgroundThread(token), token);
    }

    public void StartAtBeginning()
    {
        if (_cts is not null) return;
        _cts = new();
        _offset = 0;
        var token = _cts.Token;
        Task.Run(() => BackgroundThread(token), token);
    }

    public void Cancel()
    {
        if (_cts is null) return;
        _cts.Cancel();
        _cts.Dispose();
        _cts = null;
    }

    private async Task BackgroundThread(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var output = await Read(ct);
                if (!string.IsNullOrEmpty(output))
                    ParseAndSend(output);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch(IOException){}
            catch (Exception ex)
            {
                using(logger.BeginScope(_loggerContext))
                    logger.LogError(ex, "Could not read logs");
                return;
            }
            try { await Task.Delay(500, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        }
    }

    private async Task<string> Read(CancellationToken ct)
    {
        if (!File.Exists(LogPath)) throw new IOException("File not found" + LogPath);
        await using var fs = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (_offset < 0)
        {
            _offset = Math.Max(0, fs.Length - 65536);
            _skipPartialLine = _offset > 0;
        }
        if (fs.Length < _offset)
        {
            _offset = 0;
            _pendingLine = string.Empty;
            _skipPartialLine = false;
            _decoder.Reset();
        }
        fs.Seek(_offset, SeekOrigin.Begin);
        var bytes = new byte[(int)Math.Min(65536, fs.Length - _offset)];
        if (bytes.Length == 0) return string.Empty;
        var count = await fs.ReadAsync(bytes, ct);
        _offset += count; // File offsets count bytes, not decoded UTF-16 characters.
        var chars = new char[Encoding.UTF8.GetMaxCharCount(count)];
        var length = _decoder.GetChars(bytes, 0, count, chars, 0, flush: false);
        var text = _pendingLine + new string(chars, 0, length);
        if (_skipPartialLine)
        {
            var firstNewline = text.IndexOf('\n');
            if (firstNewline < 0) return string.Empty;
            text = text[(firstNewline + 1)..];
            _skipPartialLine = false;
        }
        var lastNewline = text.LastIndexOf('\n');
        if (lastNewline < 0 && text.Length < 65536)
        {
            _pendingLine = text;
            return string.Empty;
        }
        if (lastNewline < 0) lastNewline = text.Length - 1;
        _pendingLine = text[(lastNewline + 1)..];
        return text[..(lastNewline + 1)].TrimStart('\uFEFF');
    }

    private void ParseAndSend(string output)
    {
        using var scope = logger.BeginScope(_loggerContext);
        var lines = output.Trim().Split(["\r\n", "\r", "\n"], StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var matches = LogRegex().Match(line);
            if (!matches.Success)
            {
                logger.LogInformation(line);
                continue;
            }
            
            //var date = ParseDate(matches.Groups[1].Value);
            var source = matches.Groups[3].Value.Trim();
            var level = ParseLogLevel(matches.Groups[4].Value);
            var content = matches.Groups[5].Value;
            logger.Log(level, "{source}: {content}", source, content);
        }
    }

    // private DateTime ParseDate(string data)
    // {
    //     var matches = LogDateRegex().Match(data);
    //     if (matches.Success)
    //         return new DateTime(
    //             int.Parse(matches.Groups[1].Value),
    //             int.Parse(matches.Groups[2].Value),
    //             int.Parse(matches.Groups[3].Value),
    //             int.Parse(matches.Groups[4].Value),
    //             int.Parse(matches.Groups[5].Value),
    //             int.Parse(matches.Groups[6].Value)
    //         );
    //     return DateTime.Now;
    // }

    //Fatal, Error, Warning, Display, Log, Verbose, VeryVerbose, All (=VeryVerbose)
    private LogLevel ParseLogLevel(string data)
    {
        data = data.ToLower().Trim();
        switch (data)
        {
            case "fatal":
                return LogLevel.Critical;
            case "error":
                return LogLevel.Error;
            case "warning":
                return LogLevel.Warning;
            case "display":
            case "log":
                return LogLevel.Information;
            default:
                return LogLevel.Information;
        }
    }

    //regexr /^\[([0-9\.\-\:]+)\]\[([0-9\s]+)\]([\w\s]+):(?:([\w\s]+):)?(.+)/
    [GeneratedRegex("^\\[([0-9\\.\\-\\:]+)\\]\\[([0-9\\s]+)\\]([\\w\\s]+):(?:([\\w\\s]+):)?(.+)")]
    private static partial Regex LogRegex();
    
    // //regexr /([0-9]+)\.([0-9]+)\.([0-9]+)-([0-9]+)\.([0-9]+)\.([0-9]+):([0-9]+)/
    // [GeneratedRegex("([0-9]+)\\.([0-9]+)\\.([0-9]+)-([0-9]+)\\.([0-9]+)\\.([0-9]+):([0-9]+)")]
    // private static partial Regex LogDateRegex();
}
