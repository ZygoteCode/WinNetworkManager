using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace WinNetworkManager.Infrastructure.Logging;

/// <summary>Options of <see cref="JsonLinesFileLoggerProvider"/>.</summary>
/// <param name="Directory">Log directory.</param>
/// <param name="MinimumLevel">Minimum level written.</param>
/// <param name="RetainedFiles">Files kept (older ones are deleted).</param>
/// <param name="MaxFileBytes">Rotation threshold per file.</param>
public sealed record FileLoggerOptions(string Directory, LogLevel MinimumLevel = LogLevel.Information, int RetainedFiles = 7, long MaxFileBytes = 10 * 1024 * 1024);

/// <summary>
/// Structured logger writing one JSON object per line (<c>wnm-yyyyMMdd[.n].jsonl</c>). Records are queued and written
/// by a single background task so logging never blocks callers; when the queue is full the oldest records are dropped
/// and the number of drops is written once space is available.
/// </summary>
public sealed class JsonLinesFileLoggerProvider : ILoggerProvider
{
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly Channel<Entry> _queue = Channel.CreateBounded<Entry>(new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly Task _writer;
    private FileLoggerOptions _options;
    private long _dropped;

    /// <summary>Creates the provider and starts the writer.</summary>
    public JsonLinesFileLoggerProvider(FileLoggerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        System.IO.Directory.CreateDirectory(options.Directory);
        _writer = Task.Run(WriteLoopAsync);
    }

    /// <summary>Current minimum level.</summary>
    public LogLevel MinimumLevel => Volatile.Read(ref _options).MinimumLevel;

    /// <summary>Changes the minimum level at runtime.</summary>
    public void SetMinimumLevel(LogLevel level) => Volatile.Write(ref _options, _options with { MinimumLevel = level });

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
        _queue.Writer.TryComplete();
        try
        {
            _writer.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
            // Writer failures are not propagated at shutdown.
        }
    }

    /// <summary>Waits until queued records are written (tests).</summary>
    public async Task FlushAsync()
    {
        while (_queue.Reader.Count > 0)
        {
            await Task.Delay(10).ConfigureAwait(false);
        }

        await Task.Delay(50).ConfigureAwait(false);
    }

    private void Enqueue(Entry entry)
    {
        if (!_queue.Writer.TryWrite(entry))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    private async Task WriteLoopAsync()
    {
        StreamWriter? writer = null;
        string? currentPath = null;
        try
        {
            await foreach (var entry in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                var options = Volatile.Read(ref _options);
                string path = ResolvePath(options, entry.Timestamp);
                if (path != currentPath)
                {
                    if (writer is not null)
                    {
                        await writer.DisposeAsync().ConfigureAwait(false);
                    }

                    writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false)) { AutoFlush = false };
                    currentPath = path;
                    ApplyRetention(options);
                }

                long dropped = Interlocked.Exchange(ref _dropped, 0);
                if (dropped > 0)
                {
                    await writer!.WriteLineAsync(Format(new Entry(DateTimeOffset.UtcNow, LogLevel.Warning, nameof(JsonLinesFileLoggerProvider), $"{dropped} log records were dropped (queue full).", null, 0))).ConfigureAwait(false);
                }

                await writer!.WriteLineAsync(Format(entry)).ConfigureAwait(false);
                if (_queue.Reader.Count == 0)
                {
                    await writer.FlushAsync().ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The log directory became unwritable: stop logging to file rather than failing the application.
        }
        finally
        {
            if (writer is not null)
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static string ResolvePath(FileLoggerOptions options, DateTimeOffset timestamp)
    {
        string day = timestamp.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        for (int part = 0; ; part++)
        {
            string name = part == 0 ? $"wnm-{day}.jsonl" : $"wnm-{day}.{part}.jsonl";
            string path = Path.Combine(options.Directory, name);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < options.MaxFileBytes)
            {
                return path;
            }
        }
    }

    private static void ApplyRetention(FileLoggerOptions options)
    {
        try
        {
            var files = new DirectoryInfo(options.Directory).GetFiles("wnm-*.jsonl")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(Math.Max(1, options.RetainedFiles));
            foreach (var file in files)
            {
                file.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Retention is best effort.
        }
    }

    private static string Format(Entry entry)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, WriterOptions))
        {
            json.WriteStartObject();
            json.WriteString("t", entry.Timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            json.WriteString("level", entry.Level.ToString());
            json.WriteString("category", entry.Category);
            if (entry.EventId != 0)
            {
                json.WriteNumber("eventId", entry.EventId);
            }

            json.WriteString("message", entry.Message);
            if (entry.Exception is not null)
            {
                json.WriteString("exception", entry.Exception.ToString());
            }

            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private readonly record struct Entry(DateTimeOffset Timestamp, LogLevel Level, string Category, string Message, Exception? Exception, int EventId);

    private sealed class FileLogger(JsonLinesFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= provider.MinimumLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (!IsEnabled(logLevel))
            {
                return;
            }

            provider.Enqueue(new Entry(DateTimeOffset.UtcNow, logLevel, category, formatter(state, exception), exception, eventId.Id));
        }
    }
}
