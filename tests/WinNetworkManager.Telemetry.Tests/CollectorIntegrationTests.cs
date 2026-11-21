using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using WinNetworkManager.Platform.Windows.Processes;
using WinNetworkManager.Platform.Windows.Security;
using WinNetworkManager.Telemetry.Collection;
using WinNetworkManager.Telemetry.Ipc;
using WinNetworkManager.Telemetry.Model;

namespace WinNetworkManager.Telemetry.Tests;

/// <summary>
/// End-to-end accuracy tests against the real Kernel-Network provider: transfers of a known number of bytes must be
/// reported exactly, attributed to the right process instance, split loopback/external, per protocol and per flow.
/// Requires an elevated test host (skipped otherwise).
/// </summary>
public sealed class CollectorIntegrationTests
{
    private static bool Elevated => PrivilegeInfo.ReadCurrent().IsElevated;

    private sealed class RecordingSink : ITelemetrySink
    {
        public ConcurrentQueue<TelemetryFrame> Frames { get; } = new();

        public ConcurrentQueue<CollectorStatus> Statuses { get; } = new();

        public void OnFrame(TelemetryFrame frame) => Frames.Enqueue(frame);

        public void OnStatus(CollectorStatus status) => Statuses.Enqueue(status);
    }

    private static NetworkCollectorOptions Options(string suffix) => new() { SessionName = $"WinNetworkManager-Test-{suffix}-{Environment.ProcessId}" };

    [Fact]
    public async Task Exact_byte_counts_for_loopback_tcp_ipv4_ipv6_and_udp()
    {
        Assert.SkipUnless(Elevated, "Requires an elevated test run.");
        var sink = new RecordingSink();
        await using var collector = new NetworkCollector(Options("exact"), sink, NullLogger.Instance);
        await collector.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);

        var (client4, server4) = await TcpTransferAsync(IPAddress.Loopback, 5_000_000);
        var (client6, _) = await TcpTransferAsync(IPAddress.IPv6Loopback, 1_000_000);
        int udpReceived = await UdpTransferAsync(IPAddress.Loopback, 300, 1000);

        await Task.Delay(2000, TestContext.Current.CancellationToken);
        await collector.StopAsync();

        int me = Environment.ProcessId;
        var mine = sink.Frames.SelectMany(f => f.Processes).Where(p => p.Process.Pid == me).ToList();
        Assert.NotEmpty(mine);
        Assert.Single(mine.Select(p => p.Process).Distinct());
        Assert.Equal(ProcessSnapshotReader.ReadCreateTime(me).CreateTime, mine[0].Process.CreateTimeTicks);

        long tcpSent = mine.Sum(p => p.Counters.Loopback.TcpSent);
        long tcpReceived = mine.Sum(p => p.Counters.Loopback.TcpReceived);
        long udpSent = mine.Sum(p => p.Counters.Loopback.UdpSent);
        long udpRecv = mine.Sum(p => p.Counters.Loopback.UdpReceived);
        Assert.Equal(6_000_000, tcpSent);
        Assert.Equal(6_000_000, tcpReceived);
        Assert.Equal(300_000, udpSent);
        Assert.Equal(udpReceived * 1000L, udpRecv);
        Assert.Equal(0, mine.Sum(p => p.Counters.External.TcpReceived + p.Counters.External.TcpSent));

        // Per-flow accounting: client→server direction of the IPv4 transfer.
        var flows = sink.Frames.SelectMany(f => f.Flows).Where(f => f.Key.Process.Pid == me && f.Key.Local.Port == client4).ToList();
        Assert.Equal(5_000_000, flows.Sum(f => f.Counters.SentBytes));
        Assert.All(flows, f => Assert.Equal(server4, f.Key.Remote.Port));
        var flows6 = sink.Frames.SelectMany(f => f.Flows).Where(f => f.Key.Process.Pid == me && f.Key.Local.Port == client6).ToList();
        Assert.Equal(1_000_000, flows6.Sum(f => f.Counters.SentBytes));
        Assert.True(flows6[0].Key.Local.Address.IsIPv6);

        // Frames are contiguous and none reported losses.
        var indices = sink.Frames.Select(f => f.BucketIndex).ToList();
        Assert.Equal(indices.OrderBy(i => i), indices);
        Assert.Equal(indices.Count, indices.Distinct().Count());
        Assert.Equal(indices[^1] - indices[0] + 1, indices.Count);
        Assert.All(sink.Frames, f => Assert.False(f.Health.IsPartial));
        Assert.Contains(sink.Statuses, s => s.State == CollectorState.Running);
    }

    [Fact]
    public async Task Child_process_traffic_and_lifecycle_are_attributed_to_the_child_instance()
    {
        Assert.SkipUnless(Elevated, "Requires an elevated test run.");
        string curl = Path.Combine(Environment.SystemDirectory, "curl.exe");
        Assert.SkipUnless(File.Exists(curl), "curl.exe not available.");

        var sink = new RecordingSink();
        await using var collector = new NetworkCollector(Options("child"), sink, NullLogger.Instance);
        await collector.StartAsync(TestContext.Current.CancellationToken);

        const int Body = 2_000_000;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serve = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            var stream = socket.GetStream();
            var request = new byte[4096];
            _ = await stream.ReadAsync(request);
            byte[] header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {Body}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header);
            await stream.WriteAsync(new byte[Body]);
            return header.Length;
        });

        using var child = Process.Start(new ProcessStartInfo(curl, $"-s -o NUL http://127.0.0.1:{port}/") { UseShellExecute = false, CreateNoWindow = true })!;
        int childPid = child.Id;
        long childCreate = ProcessSnapshotReader.ReadCreateTime(childPid).CreateTime;
        await child.WaitForExitAsync(TestContext.Current.CancellationToken);
        int headerLength = await serve;
        await Task.Delay(2000, TestContext.Current.CancellationToken);
        await collector.StopAsync();

        var childTraffic = sink.Frames.SelectMany(f => f.Processes).Where(p => p.Process.Pid == childPid).ToList();
        Assert.NotEmpty(childTraffic);
        Assert.All(childTraffic, p => Assert.Equal(childCreate, p.Process.CreateTimeTicks));
        Assert.Equal(Body + headerLength, childTraffic.Sum(p => p.Counters.Loopback.TcpReceived));

        var lifecycle = sink.Frames.SelectMany(f => f.ProcessEvents).Where(e => e.Process.Pid == childPid).ToList();
        var started = Assert.Single(lifecycle, e => e.Kind == ProcessLifecycleKind.Started);
        var exited = Assert.Single(lifecycle, e => e.Kind == ProcessLifecycleKind.Exited);
        Assert.Equal(childCreate, started.Process.CreateTimeTicks);
        Assert.Equal(Environment.ProcessId, started.ParentPid);
        Assert.EndsWith("curl.exe", started.ImageName, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Path.GetPathRoot(Environment.SystemDirectory)!, started.ImageName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, exited.ExitCode);
        Assert.True(exited.Timestamp >= started.Timestamp);
    }

    [Fact]
    public async Task Pipe_host_streams_frames_to_the_verified_parent_and_shuts_down_on_request()
    {
        Assert.SkipUnless(Elevated, "Requires an elevated test run.");
        var options = new CollectorHostOptions(PipeIdentity.NewPipeName(), Environment.ProcessId, PrivilegeInfo.CurrentUserSid());
        var host = new CollectorPipeHost(options, NullLogger.Instance);
        var hostTask = host.RunAsync(Options("pipe"), CancellationToken.None);

        await using var pipe = new NamedPipeClientStream(".", options.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(10_000, TestContext.Current.CancellationToken);
        Assert.Equal(Environment.ProcessId, PipeIdentity.GetServerProcessId(pipe.SafePipeHandle));
        var hello = await TelemetryProtocol.ReadAsync(pipe, TestContext.Current.CancellationToken);
        Assert.Equal(MessageType.Hello, hello!.Value.Type);
        Assert.Equal(TelemetryProtocol.Version, TelemetryProtocol.DecodeHello(hello.Value.Payload).ProtocolVersion);

        await Task.Delay(300, TestContext.Current.CancellationToken);
        await TcpTransferAsync(IPAddress.Loopback, 750_000);

        long received = 0;
        bool sawStatus = false;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            while (received < 750_000 && !timeout.IsCancellationRequested)
            {
                var message = await TelemetryProtocol.ReadAsync(pipe, timeout.Token);
                Assert.NotNull(message);
                if (message.Value.Type == MessageType.Frame)
                {
                    var frame = TelemetryProtocol.DecodeFrame(message.Value.Payload);
                    received += frame.Processes.Where(p => p.Process.Pid == Environment.ProcessId).Sum(p => p.Counters.Loopback.TcpReceived);
                }
                else if (message.Value.Type == MessageType.Status)
                {
                    sawStatus = true;
                }
            }
        }

        Assert.Equal(750_000, received);
        Assert.True(sawStatus);
        await TelemetryProtocol.WriteAsync(pipe, MessageType.Shutdown, ReadOnlyMemory<byte>.Empty, TestContext.Current.CancellationToken);
        Assert.Equal(CollectorExitCodes.Success, await hostTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Pipe_host_rejects_a_client_that_is_not_the_parent()
    {
        Assert.SkipUnless(Elevated, "Requires an elevated test run.");
        // Parent is a different (live) process: our own connection must be refused.
        using var other = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >NUL") { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            var options = new CollectorHostOptions(PipeIdentity.NewPipeName(), other.Id, PrivilegeInfo.CurrentUserSid());
            var hostTask = new CollectorPipeHost(options, NullLogger.Instance).RunAsync(Options("reject"), CancellationToken.None);
            await using var pipe = new NamedPipeClientStream(".", options.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(10_000, TestContext.Current.CancellationToken);
            Assert.Equal(CollectorExitCodes.UnexpectedClient, await hostTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        }
        finally
        {
            other.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task Collector_restarts_after_the_session_is_stopped_externally()
    {
        Assert.SkipUnless(Elevated, "Requires an elevated test run.");
        var sink = new RecordingSink();
        var options = Options("restart");
        await using var collector = new NetworkCollector(options, sink, NullLogger.Instance);
        await collector.StartAsync(TestContext.Current.CancellationToken);
        Assert.True(Platform.Windows.Etw.EtwRealTimeSession.TryStopExisting(options.SessionName));

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && !(collector.State == CollectorState.Running && collector.Status.Restarts > 0))
        {
            await Task.Delay(200, TestContext.Current.CancellationToken);
        }

        Assert.Equal(CollectorState.Running, collector.State);
        Assert.Equal(1, collector.Status.Restarts);
        Assert.Contains(sink.Statuses, s => s.State == CollectorState.Retrying);
    }

    private static async Task<(int ClientPort, int ServerPort)> TcpTransferAsync(IPAddress address, int total)
    {
        using var listener = new TcpListener(address, 0);
        listener.Start();
        int serverPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var s = await listener.AcceptTcpClientAsync();
            var stream = s.GetStream();
            var buffer = new byte[65536];
            long got = 0;
            int n;
            while ((n = await stream.ReadAsync(buffer)) > 0)
            {
                got += n;
            }

            return got;
        });

        using var client = new TcpClient(address.AddressFamily);
        await client.ConnectAsync(address, serverPort);
        int clientPort = ((IPEndPoint)client.Client.LocalEndPoint!).Port;
        var stream = client.GetStream();
        var data = new byte[65536];
        int sent = 0;
        while (sent < total)
        {
            int k = Math.Min(data.Length, total - sent);
            await stream.WriteAsync(data.AsMemory(0, k));
            sent += k;
        }

        client.Client.Shutdown(SocketShutdown.Send);
        Assert.Equal(total, await server);
        return (clientPort, serverPort);
    }

    private static async Task<int> UdpTransferAsync(IPAddress address, int count, int size)
    {
        using var server = new UdpClient(new IPEndPoint(address, 0));
        server.Client.ReceiveBufferSize = 4 * 1024 * 1024;
        var endpoint = (IPEndPoint)server.Client.LocalEndPoint!;
        using var client = new UdpClient(address.AddressFamily);
        int received = 0;
        using var cts = new CancellationTokenSource(5000);
        var receiver = Task.Run(async () =>
        {
            try
            {
                while (received < count)
                {
                    await server.ReceiveAsync(cts.Token);
                    received++;
                }
            }
            catch (OperationCanceledException)
            {
                // Some datagrams may be dropped by the socket buffer; the test compares with what was received.
            }
        });

        var payload = new byte[size];
        for (int i = 0; i < count; i++)
        {
            await client.SendAsync(payload, size, endpoint);
            if (i % 50 == 0)
            {
                await Task.Delay(1);
            }
        }

        await receiver;
        return received;
    }
}
