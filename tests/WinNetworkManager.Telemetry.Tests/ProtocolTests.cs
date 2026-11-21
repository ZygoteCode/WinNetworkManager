using WinNetworkManager.Core.Networking;
using WinNetworkManager.Core.Processes;
using WinNetworkManager.Core.Traffic;
using WinNetworkManager.Telemetry.Ipc;
using WinNetworkManager.Telemetry.Model;

namespace WinNetworkManager.Telemetry.Tests;

public sealed class ProtocolTests
{
    private static TelemetryFrame SampleFrame()
    {
        IpEndpointValue.TryParse("[2001:db8::5]:443", out var remote6);
        IpEndpointValue.TryParse("10.0.0.2:50000", out var local4);
        return new TelemetryFrame
        {
            BucketIndex = 123_456,
            StartTimestamp = 1_000,
            EndTimestamp = 3_500,
            Processes =
            [
                new ProcessTraffic(new ProcessKey(42, 133_000_000_000_000_001), new TrafficCounters(new ProtocolBytes(1, 2, 3, 4), new ProtocolBytes(5, 6, 7, 8), 9, 10)),
                new ProcessTraffic(new ProcessKey(4, 0), default),
            ],
            Flows =
            [
                new FlowTraffic(new FlowKey(TransportProtocol.Tcp, new ProcessKey(42, 1), local4, remote6), new FlowCounters { ReceivedBytes = 100, SentBytes = 200, ReceiveEvents = 3, SendEvents = 4, RetransmittedBytes = 5, Retransmissions = 6 }),
                new FlowTraffic(new FlowKey(TransportProtocol.Udp, new ProcessKey(7, 2), default, local4), default),
            ],
            ProcessEvents =
            [
                new ProcessLifecycleEvent(ProcessLifecycleKind.Started, 1_100, new ProcessKey(99, 77), 42, 1, @"C:\Windows\notepad.exe", null, 1234),
                new ProcessLifecycleEvent(ProcessLifecycleKind.Exited, 1_200, new ProcessKey(98, 76), 0, -1, null, -1073741510, 0),
            ],
            Health = new FrameHealth(10, 1, 2, 3, 4),
        };
    }

    [Fact]
    public void Frame_round_trips_exactly()
    {
        var frame = SampleFrame();
        var decoded = TelemetryProtocol.DecodeFrame(TelemetryProtocol.EncodeFrame(frame));
        Assert.Equal(frame.BucketIndex, decoded.BucketIndex);
        Assert.Equal(frame.StartTimestamp, decoded.StartTimestamp);
        Assert.Equal(frame.EndTimestamp, decoded.EndTimestamp);
        Assert.Equal(frame.Processes, decoded.Processes);
        Assert.Equal(frame.Flows, decoded.Flows);
        Assert.Equal(frame.ProcessEvents, decoded.ProcessEvents);
        Assert.Equal(frame.Health, decoded.Health);
    }

    [Fact]
    public void Status_hello_and_log_round_trip()
    {
        var status = new CollectorStatus(CollectorState.Running, "S", 12.5, 100, 2, 3, 4, 5, true, "boom", false);
        Assert.Equal(status, TelemetryProtocol.DecodeStatus(TelemetryProtocol.EncodeStatus(status)));
        var hello = new HelloMessage(TelemetryProtocol.Version, 77, 10_000_000, 2_500_000, "WinNetworkManager-Collector");
        Assert.Equal(hello, TelemetryProtocol.DecodeHello(TelemetryProtocol.EncodeHello(hello)));
        var log = new LogMessage(3, "ünïcødé ✓");
        Assert.Equal(log, TelemetryProtocol.DecodeLog(TelemetryProtocol.EncodeLog(log)));
    }

    [Fact]
    public void Every_truncation_of_a_frame_is_rejected()
    {
        byte[] bytes = TelemetryProtocol.EncodeFrame(SampleFrame());
        for (int length = 0; length < bytes.Length; length++)
        {
            Assert.Throws<InvalidDataException>(() => TelemetryProtocol.DecodeFrame(bytes.AsSpan(0, length)));
        }
    }

    [Fact]
    public void Trailing_bytes_and_absurd_counts_are_rejected()
    {
        byte[] bytes = TelemetryProtocol.EncodeFrame(SampleFrame());
        Assert.Throws<InvalidDataException>(() => TelemetryProtocol.DecodeFrame([.. bytes, 0]));

        var hostile = new byte[28];
        BitConverter.GetBytes(int.MaxValue).CopyTo(hostile, 24); // process count
        Assert.Throws<InvalidDataException>(() => TelemetryProtocol.DecodeFrame(hostile));
    }

    [Fact]
    public async Task Stream_framing_round_trips_and_detects_bad_lengths()
    {
        using var stream = new MemoryStream();
        await TelemetryProtocol.WriteAsync(stream, MessageType.Log, TelemetryProtocol.EncodeLog(new LogMessage(2, "a")), CancellationToken.None);
        await TelemetryProtocol.WriteAsync(stream, MessageType.Shutdown, ReadOnlyMemory<byte>.Empty, CancellationToken.None);
        stream.Position = 0;
        var first = await TelemetryProtocol.ReadAsync(stream, CancellationToken.None);
        var second = await TelemetryProtocol.ReadAsync(stream, CancellationToken.None);
        var end = await TelemetryProtocol.ReadAsync(stream, CancellationToken.None);
        Assert.Equal(MessageType.Log, first!.Value.Type);
        Assert.Equal(MessageType.Shutdown, second!.Value.Type);
        Assert.Null(end);

        using var bad = new MemoryStream([0xFF, 0xFF, 0xFF, 0x7F, 2]);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await TelemetryProtocol.ReadAsync(bad, CancellationToken.None));
    }

    [Fact]
    public void Host_arguments_are_validated_strictly()
    {
        string pipe = PipeIdentity.NewPipeName();
        var options = new CollectorHostOptions(pipe, 1234, "S-1-5-21-1-2-3-1001");
        Assert.Equal(options, CollectorHostOptions.Parse(options.ToArguments()));
        Assert.Null(CollectorHostOptions.Parse(["--pipe", "evil", "--parent", "1", "--client-sid", "S-1-5-18"]));
        Assert.Null(CollectorHostOptions.Parse(["--pipe", pipe, "--parent", "-1", "--client-sid", "S-1-5-18"]));
        Assert.Null(CollectorHostOptions.Parse(["--pipe", pipe, "--parent", "1", "--client-sid", "not-a-sid"]));
        Assert.Null(CollectorHostOptions.Parse(["--pipe", pipe, "--parent", "1", "--client-sid", "S-1-5-18", "--extra", "x"]));
    }
}
