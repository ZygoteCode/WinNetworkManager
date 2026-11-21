using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using WinNetworkManager.Core.Networking;
using WinNetworkManager.Core.Processes;
using WinNetworkManager.Core.Time;
using WinNetworkManager.Platform.Windows.Etw;
using WinNetworkManager.Telemetry.Collection;
using WinNetworkManager.Telemetry.Decoding;
using WinNetworkManager.Telemetry.Ipc;
using WinNetworkManager.Telemetry.Model;

namespace WinNetworkManager.Telemetry.Tests;

/// <summary>
/// Throughput and allocation budgets of the hot path (the ETW callback must keep up with busy machines), and fuzzing of
/// the pipe protocol (the unprivileged window must survive anything the helper side could send). Budgets are generous
/// enough for Debug builds on modest hardware; a failure means an order-of-magnitude regression.
/// </summary>
[Trait("Category", "Performance")]
public sealed class PerformanceAndRobustnessTests
{
    private static TdhProperty U32(string name) => new(name, TdhInType.UInt32, 8, TdhPropertyFlags.None, 4, 1);

    private static TdhProperty U16(string name) => new(name, TdhInType.UInt16, 22, TdhPropertyFlags.None, 2, 1);

    [Fact]
    public void Decoder_decodes_a_million_events_quickly_without_allocating()
    {
        var schema = TdhSchema.FromProperties("TCPIP", "Recv", U32("PID"), U32("size"), U32("daddr"), U32("saddr"), U16("dport"), U16("sport"), U32("seqnum"), U32("connid"));
        var payload = new byte[28];
        BinaryPrimitives.WriteInt32LittleEndian(payload, 4321);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), 1460);
        IPAddress.Parse("93.184.216.34").GetAddressBytes().CopyTo(payload, 8);
        IPAddress.Parse("192.168.1.10").GetAddressBytes().CopyTo(payload, 12);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(16), 443);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(18), 50123);
        var decoder = new KernelNetworkDecoder();
        Assert.True(decoder.TryDecode(11, 0, 0, payload, schema, out _)); // builds and caches the layout

        long size = 0;
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 1_000_000; i++)
        {
            decoder.TryDecode(11, 0, i, payload, schema, out var ev);
            size += ev.Size;
        }

        watch.Stop();
        Assert.Equal(1460L * 1_000_000, size);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.True(bytes < 16 * 1024, $"{bytes} bytes allocated: the decoder allocates per event"); // any per-event allocation would be megabytes
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"1M decodes took {watch.Elapsed.TotalMilliseconds:0} ms");
    }

    [Fact]
    public void Bucketer_aggregates_heavy_traffic_within_budget_and_exactly()
    {
        const long frequency = 10_000_000;
        var timeline = new BucketTimeline(frequency, TimeSpan.FromMilliseconds(250));
        var bucketer = new TrafficBucketer(timeline, TimeSpan.FromSeconds(1), 20_000);
        bucketer.Start(1_000 * frequency);
        IpEndpointValue.TryParse("192.168.1.2:50000", out var local);
        var processes = Enumerable.Range(0, 200).Select(i => new ProcessKey(1000 + i, 77 + i)).ToArray();
        var remotes = Enumerable.Range(0, 2_000).Select(i => new IpEndpointValue(IpAddressValue.FromIPv4(10, 1, (byte)(i / 250), (byte)(i % 250)), 443)).ToArray();

        const int events = 600_000; // 10 s at 60 000 events/s
        long totalBytes = 0;
        var watch = Stopwatch.StartNew();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var frames = new List<TelemetryFrame>();
        for (int i = 0; i < events; i++)
        {
            long timestamp = 1_000 * frequency + (i * (10 * frequency / events));
            var ev = new NetworkEvent
            {
                Kind = (i & 1) == 0 ? NetworkEventKind.TcpReceive : NetworkEventKind.UdpSend,
                Timestamp = timestamp,
                Size = 100 + (i % 1400),
                Pid = processes[i % processes.Length].Pid,
                Local = local,
                Remote = remotes[i % remotes.Length],
            };
            totalBytes += ev.Size;
            bucketer.AddNetworkEvent(ev, processes[i % processes.Length], loopback: false);
            if (i % 10_000 == 0)
            {
                frames.AddRange(bucketer.Finalize(timestamp));
            }
        }

        frames.AddRange(bucketer.FinalizeAll(1_000 * frequency + (11 * frequency)));
        watch.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        long counted = frames.SelectMany(f => f.Processes).Sum(p => p.Counters.External.TcpReceived + p.Counters.External.UdpSent);
        Assert.Equal(totalBytes, counted); // no byte lost or duplicated under load
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"600k events took {watch.Elapsed.TotalMilliseconds:0} ms");
        Assert.True(allocated < 256L * 1024 * 1024, $"allocated {allocated / (1024 * 1024)} MB");
    }

    [Fact]
    public void Protocol_decoders_reject_random_input_with_a_single_exception_type()
    {
        var random = new Random(20261121);
        Func<ReadOnlyMemory<byte>, object>[] decoders =
        [
            p => TelemetryProtocol.DecodeFrame(p.Span),
            p => TelemetryProtocol.DecodeStatus(p.Span),
            p => TelemetryProtocol.DecodeHello(p.Span),
            p => TelemetryProtocol.DecodeLog(p.Span),
        ];
        int rejected = 0;
        for (int i = 0; i < 20_000; i++)
        {
            var bytes = new byte[random.Next(0, 512)];
            random.NextBytes(bytes);
            try
            {
                _ = decoders[i % decoders.Length](bytes);
            }
            catch (InvalidDataException)
            {
                rejected++;
            }
        }

        Assert.True(rejected > 15_000, $"only {rejected} inputs were rejected");
    }
}
