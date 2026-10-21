using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Trebuchet;

public class ConsoleWriter : TextWriter
{
    public ConsoleWriter(int buffer, int totalText)
    {
        _totalText = totalText;
        _builder = new(buffer, buffer);
    }

    private readonly StringBuilder _builder;
    private readonly int _totalText;
    // Content kept in a builder: appending a flush no longer copies the whole (up to 500k chars) text, which
    // caused large object heap churn on busy servers running for days.
    private readonly StringBuilder _content = new();
    private string? _textCache;

    public event EventHandler<string>? TextFlushed;

    /// <summary>Kept text (last <c>totalText</c> characters, give or take the trim slack).</summary>
    public string Text => _textCache ??= _content.Length <= _totalText
        ? _content.ToString()
        : _content.ToString(_content.Length - _totalText, _totalText);

    public override Encoding Encoding { get; } = Encoding.UTF8;

    public override void Flush()
    {
        var flush = _builder.ToString();
        _builder.Clear();
        if (flush.Length == 0) return;

        _content.Append(flush);
        // Trim rarely (25% slack) instead of on every flush.
        if (_content.Length > _totalText + _totalText / 4)
            _content.Remove(0, _content.Length - _totalText);
        _textCache = null;
        TextFlushed?.Invoke(this, flush);
    }

    public override void Write(char value)
    {
        _builder.Append(value);
        if (_builder.Length == _builder.MaxCapacity)
            Flush();
    }

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        var span = value.AsSpan();
        while (span.Length > 0)
        {
            var free = _builder.MaxCapacity - _builder.Length;
            var take = Math.Min(free, span.Length);
            _builder.Append(span[..take]);
            span = span[take..];
            if (_builder.Length == _builder.MaxCapacity)
                Flush();
        }
    }

    // TextWriter runs its async methods on a thread pool thread: a flush then raised TextFlushed off the UI thread
    // (the console editor throws there, so e.g. an RCon command was not sent) and raced the builders. The console
    // writers are only used on the UI thread, so the async methods write synchronously.
    public override Task WriteAsync(char value) => Sync(() => Write(value));
    public override Task WriteAsync(string? value) => Sync(() => Write(value));
    public override Task WriteAsync(char[] buffer, int index, int count) => Sync(() => Write(buffer, index, count));
    public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        => Sync(() => Write(buffer.Span));
    public override Task WriteLineAsync() => Sync(WriteLine);
    public override Task WriteLineAsync(char value) => Sync(() => WriteLine(value));
    public override Task WriteLineAsync(string? value) => Sync(() => WriteLine(value));
    public override Task WriteLineAsync(char[] buffer, int index, int count) => Sync(() => WriteLine(buffer, index, count));
    public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        => Sync(() => WriteLine(buffer.Span));
    public override Task FlushAsync() => Sync(Flush);

    private static Task Sync(Action write)
    {
        try
        {
            write();
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }
    }

    public void Clear()
    {
        _builder.Clear();
        _content.Clear();
        _textCache = null;
    }
}
