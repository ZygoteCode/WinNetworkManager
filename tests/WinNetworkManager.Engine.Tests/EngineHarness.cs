using Microsoft.Extensions.Logging.Abstractions;
using WinNetworkManager.Core.Networking;
using WinNetworkManager.Core.Processes;
using WinNetworkManager.Core.Settings;
using WinNetworkManager.Core.Traffic;
using WinNetworkManager.Engine.Interfaces;
using WinNetworkManager.Engine.Model;
using WinNetworkManager.Platform.Windows.IpHelper;
using WinNetworkManager.Platform.Windows.Processes;
using WinNetworkManager.Telemetry.Model;

namespace WinNetworkManager.Engine.Tests;

/// <summary>Deterministic clock: 10 MHz timestamps, explicit advancement.</summary>
internal sealed class ManualTime : TimeProvider
{
    public const long Frequency = 10_000_000;

    public long Ticks { get; set; } = 1_000 * Frequency;

    public DateTimeOffset Utc { get; set; } = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    public override long TimestampFrequency => Frequency;

    public override long GetTimestamp() => Ticks;

    public override DateTimeOffset GetUtcNow() => Utc;

    public void Advance(TimeSpan span)
    {
        Ticks += (long)(span.TotalSeconds * Frequency);
        Utc += span;
    }
}

internal sealed class FakeProcesses : IProcessSnapshotSource
{
    public List<ProcessSnapshotEntry> Entries { get; } = [];

    public IReadOnlyList<ProcessSnapshotEntry> ReadProcesses() => Entries.ToArray();
}

internal sealed class FakeConnections : IConnectionTableSource
{
    public List<TcpConnectionRow> Tcp { get; } = [];

    public List<UdpEndpointRow> Udp { get; } = [];

    public void ReadTcp(List<TcpConnectionRow> destination)
    {
        destination.Clear();
        destination.AddRange(Tcp);
    }

    public void ReadUdp(List<UdpEndpointRow> destination)
    {
        destination.Clear();
        destination.AddRange(Udp);
    }
}

internal sealed class FakeInterfaces : IInterfaceTableSource
{
    public List<InterfaceRow> Rows { get; } = [];

    public IReadOnlyList<InterfaceRow> ReadInterfaces() => Rows.ToArray();
}

internal sealed class FakeConfiguration : IInterfaceConfigurationSource
{
    public List<InterfaceConfiguration> Items { get; } = [];

    public IReadOnlyList<InterfaceConfiguration> Read() => Items.ToArray();
}

/// <summary>Builds an engine wired to fakes and drives it cycle by cycle.</summary>
internal sealed class EngineHarness : IAsyncDisposable
{
    public const long BucketTicks = ManualTime.Frequency / 4; // 250 ms

    public EngineHarness(AppSettings? settings = null)
    {
        Settings = settings ?? new AppSettings
        {
            Monitoring = new MonitoringSettings { RefreshIntervalMs = 1000, ConnectionPollIntervalMs = 1000, ProcessPollIntervalMs = 1000, ExitedProcessRetentionSeconds = 30 },
        };
        Engine = new MonitoringEngine(
            Settings,
            NullLoggerFactory.Instance,
            Time,
            new MonitoringEngineSources
            {
                Processes = Processes,
                Connections = Connections,
                Interfaces = Interfaces,
                Configuration = Configuration,
                ReadServices = false,
                Enrich = false,
            });
    }

    public AppSettings Settings { get; }

    public ManualTime Time { get; } = new();

    public FakeProcesses Processes { get; } = new();

    public FakeConnections Connections { get; } = new();

    public FakeInterfaces Interfaces { get; } = new();

    public FakeConfiguration Configuration { get; } = new();

    public MonitoringEngine Engine { get; }

    private ITelemetrySink Sink => Engine;

    public long CurrentBucket => Time.Ticks / BucketTicks;

    public void CollectorRunning() => Sink.OnStatus(new CollectorStatus(CollectorState.Running, "test", 0, 0, 0, 0, 0, 0, true, null, false));

    public void CollectorStopped() => Sink.OnStatus(CollectorStatus.NotRunning("test"));

    public void Frame(long bucket, IReadOnlyList<ProcessTraffic>? processes = null, IReadOnlyList<FlowTraffic>? flows = null, IReadOnlyList<ProcessLifecycleEvent>? events = null, long lost = 0) =>
        Sink.OnFrame(new TelemetryFrame
        {
            BucketIndex = bucket,
            StartTimestamp = bucket * BucketTicks,
            EndTimestamp = (bucket + 1) * BucketTicks,
            Processes = processes ?? [],
            Flows = flows ?? [],
            ProcessEvents = events ?? [],
            Health = new FrameHealth(0, 0, lost, 0, 0),
        });

    /// <summary>Emits frames for the buckets of the last second (ending one second before "now").</summary>
    public long FramesForLastSecond(Func<long, IReadOnlyList<ProcessTraffic>?>? traffic = null, long? lostInFirst = null)
    {
        long end = CurrentBucket - 4; // finalization delay ≈ 1 s
        for (long b = end - 3; b <= end; b++)
        {
            Frame(b, traffic?.Invoke(b), lost: b == end - 3 ? lostInFirst ?? 0 : 0);
        }

        return end;
    }

    public EngineSnapshot Cycle(TimeSpan? advance = null)
    {
        Time.Advance(advance ?? TimeSpan.FromSeconds(1));
        return Engine.RunCycle();
    }

    public static ProcessSnapshotEntry Proc(int pid, long created, string name, int parent = 1) =>
        new(pid, parent, name, 1, created, created > 0 ? InfoStatus.Available : InfoStatus.AccessDenied);

    public static TrafficCounters Bytes(long tcpRx = 0, long tcpTx = 0, long udpRx = 0, long udpTx = 0, long loopRx = 0, long loopTx = 0) =>
        new(new ProtocolBytes(tcpRx, tcpTx, udpRx, udpTx), new ProtocolBytes(loopRx, loopTx, 0, 0));

    public static IpEndpointValue Ep(string text)
    {
        Assert.True(IpEndpointValue.TryParse(text, out var ep), text);
        return ep;
    }

    public static InterfaceRow Nic(ulong luid, Guid guid, ulong inOctets, ulong outOctets, bool hardware = true, bool filter = false, uint type = 6) => new()
    {
        Luid = luid,
        Index = (uint)luid,
        Guid = guid,
        Alias = "Ethernet " + luid,
        Description = "Test adapter",
        IfType = type,
        PhysicalMediumType = 14,
        IsHardware = hardware,
        IsFilter = filter,
        ConnectorPresent = true,
        OperStatus = InterfaceOperationalStatus.Up,
        MediaConnected = true,
        Mtu = 1500,
        TransmitLinkSpeed = 1_000_000_000,
        ReceiveLinkSpeed = 1_000_000_000,
        InOctets = inOctets,
        OutOctets = outOctets,
        InPackets = 0,
        OutPackets = 0,
        InDiscards = 0,
        InErrors = 0,
        OutDiscards = 0,
        OutErrors = 0,
        PhysicalAddress = [0, 1, 2, 3, 4, 5],
    };

    public ValueTask DisposeAsync() => Engine.DisposeAsync();
}
