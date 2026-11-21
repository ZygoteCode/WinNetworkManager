using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using WinNetworkManager.Core.Time;
using WinNetworkManager.Core.Traffic;
using WinNetworkManager.Infrastructure.History;

namespace WinNetworkManager.Engine.History;

/// <summary>
/// Turns live accounting into history: per-second system samples (kept in memory for the recent window and, when
/// persistence is enabled, written to disk) and per-minute application/interface/system aggregates. Values are byte
/// counts per interval with quality flags; nothing about remote endpoints is recorded. Engine-thread only, except the
/// background writer.
/// </summary>
internal sealed class HistoryRecorder : IAsyncDisposable
{
    private readonly BucketTimeline _timeline;
    private readonly int _bucketsPerSecond;
    private readonly ILogger _logger;
    private readonly Dictionary<long, SecondAccumulator> _seconds = [];
    private readonly Dictionary<long, MinuteAccumulator> _minutes = [];
    private readonly Channel<HistoryBatch> _queue = Channel.CreateBounded<HistoryBatch>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly LinkedList<SystemSample> _recent = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writer;
    private IHistorySink? _sink;
    private int _capacitySeconds;
    private int _writeFailures;

    public HistoryRecorder(BucketTimeline timeline, int bucketsPerSecond, int inMemorySeconds, ILogger logger)
    {
        _timeline = timeline;
        _bucketsPerSecond = bucketsPerSecond;
        _capacitySeconds = inMemorySeconds;
        _logger = logger;
        _writer = Task.Run(WriteLoopAsync);
    }

    /// <summary>Sink receiving batches (null = memory only).</summary>
    public IHistorySink? Sink
    {
        get => Volatile.Read(ref _sink);
        set
        {
            Volatile.Write(ref _sink, value);
            _writeFailures = 0;
        }
    }

    /// <summary>Recent per-second samples kept in memory.</summary>
    public int InMemorySeconds
    {
        get => _capacitySeconds;
        set => _capacitySeconds = Math.Max(60, value);
    }

    /// <summary>Batches dropped because the writer could not keep up or failed.</summary>
    public long DroppedBatches { get; private set; }

    /// <summary>Attributed traffic of one finalized bucket (sum over processes).</summary>
    public void OnBucket(long bucket, TrafficCounters counters, bool partial)
    {
        var acc = Second(bucket / _bucketsPerSecond);
        acc.External += counters.External;
        acc.Loopback += counters.Loopback;
        acc.CoveredBuckets++;
        acc.Partial |= partial;
    }

    /// <summary>Traffic of one application in one bucket.</summary>
    public void OnApp(long bucket, string appKey, string name, string? path, ProtocolBytes bytes)
    {
        if (bytes.IsZero)
        {
            return;
        }

        long minute = MinuteOf(_timeline.ToUtc(_timeline.StartOf(bucket)));
        var acc = Minute(minute);
        if (!acc.Apps.TryGetValue(appKey, out var app))
        {
            app = new AppAccumulator(name, path);
            acc.Apps[appKey] = app;
        }

        app.Bytes += bytes;
    }

    /// <summary>Counter delta of an interface between two samples.</summary>
    public void OnInterface(Guid id, string name, long previousTimestamp, long timestamp, long received, long sent, bool physical)
    {
        long mid = previousTimestamp + ((timestamp - previousTimestamp) / 2);
        if (physical)
        {
            var second = Second(mid / _timeline.Frequency);
            second.PhysicalReceived += received;
            second.PhysicalSent += sent;
            second.HasPhysical = true;
        }

        long minute = MinuteOf(_timeline.ToUtc(mid));
        var acc = Minute(minute);
        if (!acc.Interfaces.TryGetValue(id, out var nic))
        {
            nic = new InterfaceAccumulator(name);
            acc.Interfaces[id] = nic;
        }

        nic.Received += received;
        nic.Sent += sent;
    }

    /// <summary>Seconds after the end of a minute before it is written (late buckets and the 3 s second lag).</summary>
    internal const int MinuteGraceSeconds = 5;

    /// <summary>
    /// Emits every complete second and minute. Call once per engine cycle. With <paramref name="final"/> (shutdown),
    /// everything still accumulated is written, including the current minute: the store adds rows for the same
    /// interval together, so a minute continued after a restart stays exact.
    /// </summary>
    public void Flush(long nowTimestamp, bool collectorRunning, bool final = false)
    {
        long nowSecond = nowTimestamp / _timeline.Frequency;
        var systemRows = new List<SystemSample>();
        foreach (long second in _seconds.Keys.Where(s => final || s < nowSecond - 3).Order().ToList())
        {
            var acc = _seconds[second];
            _seconds.Remove(second);
            bool attributed = acc.CoveredBuckets > 0;
            var flags = (attributed ? SampleFlags.None : SampleFlags.NoCollector)
                | (acc.Partial || (attributed && acc.CoveredBuckets < _bucketsPerSecond) ? SampleFlags.Partial : SampleFlags.None)
                | (acc.HasPhysical ? SampleFlags.None : SampleFlags.NoInterfaceData);
            var start = Floor(_timeline.ToUtc(second * _timeline.Frequency));
            var sample = new SystemSample(
                start,
                1,
                acc.HasPhysical ? acc.PhysicalReceived : null,
                acc.HasPhysical ? acc.PhysicalSent : null,
                attributed ? acc.External.TcpReceived : null,
                attributed ? acc.External.TcpSent : null,
                attributed ? acc.External.UdpReceived : null,
                attributed ? acc.External.UdpSent : null,
                attributed ? acc.Loopback.Received : null,
                attributed ? acc.Loopback.Sent : null,
                flags);
            systemRows.Add(sample);
            _recent.AddLast(sample);
            while (_recent.Count > _capacitySeconds)
            {
                _recent.RemoveFirst();
            }

            var minute = Minute(MinuteOf(start));
            minute.Add(sample);
        }

        long nowUnix = _timeline.ToUtc(nowTimestamp).ToUnixTimeSeconds();
        var apps = new List<AppSample>();
        var interfaces = new List<InterfaceSample>();
        foreach (long minute in _minutes.Keys.Where(m => final || ((m + 1) * 60) + MinuteGraceSeconds <= nowUnix).Order().ToList())
        {
            var acc = _minutes[minute];
            _minutes.Remove(minute);
            var start = DateTimeOffset.FromUnixTimeSeconds(minute * 60);
            if (acc.Seconds > 0)
            {
                systemRows.Add(new SystemSample(
                    start,
                    60,
                    acc.HasPhysical ? acc.PhysicalReceived : null,
                    acc.HasPhysical ? acc.PhysicalSent : null,
                    acc.AttributedSeconds > 0 ? acc.External.TcpReceived : null,
                    acc.AttributedSeconds > 0 ? acc.External.TcpSent : null,
                    acc.AttributedSeconds > 0 ? acc.External.UdpReceived : null,
                    acc.AttributedSeconds > 0 ? acc.External.UdpSent : null,
                    acc.AttributedSeconds > 0 ? acc.Loopback.Received : null,
                    acc.AttributedSeconds > 0 ? acc.Loopback.Sent : null,
                    acc.Flags));
            }

            foreach (var (key, app) in acc.Apps)
            {
                apps.Add(new AppSample(start, key, app.Name, app.Path, app.Bytes.TcpReceived, app.Bytes.TcpSent, app.Bytes.UdpReceived, app.Bytes.UdpSent));
            }

            foreach (var (id, nic) in acc.Interfaces)
            {
                interfaces.Add(new InterfaceSample(start, id, nic.Name, nic.Received, nic.Sent));
            }
        }

        // Bound memory if the clock jumps far (accumulators that can never complete are dropped).
        if (_seconds.Count > 600)
        {
            foreach (long stale in _seconds.Keys.Order().Take(_seconds.Count - 600).ToList())
            {
                _seconds.Remove(stale);
            }
        }

        if (_minutes.Count > 30)
        {
            foreach (long stale in _minutes.Keys.Order().Take(_minutes.Count - 30).ToList())
            {
                _minutes.Remove(stale);
            }
        }

        _ = collectorRunning;
        if (Sink is not null && (systemRows.Count > 0 || apps.Count > 0 || interfaces.Count > 0))
        {
            if (!_queue.Writer.TryWrite(new HistoryBatch(systemRows, apps, interfaces)))
            {
                DroppedBatches++;
            }
        }
    }

    /// <summary>Recent per-second samples within <paramref name="range"/> of the newest one.</summary>
    public IReadOnlyList<SystemSample> Recent(TimeSpan range)
    {
        if (_recent.Count == 0)
        {
            return [];
        }

        var cutoff = _recent.Last!.Value.Start - range;
        return _recent.Where(s => s.Start > cutoff).ToArray();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        try
        {
            await _writer.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        _cts.Dispose();
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var batch in _queue.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                var sink = Sink;
                if (sink is null)
                {
                    continue;
                }

                try
                {
                    sink.Write(batch);
                    _writeFailures = 0;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    DroppedBatches++;
                    if (++_writeFailures <= 3)
                    {
                        _logger.LogError(ex, "Writing traffic history failed.");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
    }

    private SecondAccumulator Second(long second)
    {
        if (!_seconds.TryGetValue(second, out var acc))
        {
            acc = new SecondAccumulator();
            _seconds[second] = acc;
        }

        return acc;
    }

    private MinuteAccumulator Minute(long minute)
    {
        if (!_minutes.TryGetValue(minute, out var acc))
        {
            acc = new MinuteAccumulator();
            _minutes[minute] = acc;
        }

        return acc;
    }

    private static long MinuteOf(DateTimeOffset time) => (long)Math.Floor(time.ToUnixTimeSeconds() / 60.0);

    private static DateTimeOffset Floor(DateTimeOffset time) => DateTimeOffset.FromUnixTimeSeconds(time.ToUnixTimeSeconds());

    private sealed class SecondAccumulator
    {
        public ProtocolBytes External;
        public ProtocolBytes Loopback;
        public int CoveredBuckets;
        public bool Partial;
        public long PhysicalReceived;
        public long PhysicalSent;
        public bool HasPhysical;
    }

    private sealed class MinuteAccumulator
    {
        public ProtocolBytes External;
        public ProtocolBytes Loopback;
        public long PhysicalReceived;
        public long PhysicalSent;
        public bool HasPhysical;
        public int Seconds;
        public int AttributedSeconds;
        public SampleFlags Flags;
        public readonly Dictionary<string, AppAccumulator> Apps = new(StringComparer.Ordinal);
        public readonly Dictionary<Guid, InterfaceAccumulator> Interfaces = [];

        public void Add(SystemSample s)
        {
            Seconds++;
            if (s.PhysicalReceived is { } prx)
            {
                PhysicalReceived += prx;
                PhysicalSent += s.PhysicalSent ?? 0;
                HasPhysical = true;
            }

            if (s.TcpReceived is not null)
            {
                AttributedSeconds++;
                External += new ProtocolBytes(s.TcpReceived ?? 0, s.TcpSent ?? 0, s.UdpReceived ?? 0, s.UdpSent ?? 0);
                Loopback += new ProtocolBytes(s.LoopbackReceived ?? 0, s.LoopbackSent ?? 0, 0, 0);
            }

            Flags |= s.Flags;
        }
    }

    private sealed class AppAccumulator(string name, string? path)
    {
        public string Name { get; } = name;

        public string? Path { get; } = path;

        public ProtocolBytes Bytes;
    }

    private sealed class InterfaceAccumulator(string name)
    {
        public string Name { get; } = name;

        public long Received;

        public long Sent;
    }
}
