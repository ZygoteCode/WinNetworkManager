using System.Diagnostics;
using WinNetworkManager.Core.Networking;
using WinNetworkManager.Core.Processes;
using WinNetworkManager.Platform.Windows.IpHelper;
using WinNetworkManager.Telemetry.Model;
using static WinNetworkManager.Engine.Tests.EngineHarness;
using TcpState = WinNetworkManager.Core.Networking.TcpState;

namespace WinNetworkManager.Engine.Tests;

/// <summary>
/// A busy machine: 500 processes, 5 000 TCP connections, 500 UDP endpoints and traffic for every process in every
/// bucket. A cycle must stay far below the 1 s refresh interval and every byte must still be counted.
/// </summary>
[Trait("Category", "Performance")]
public sealed class LoadTests
{
    [Fact]
    public async Task Engine_cycle_stays_fast_and_exact_under_load()
    {
        await using var h = new EngineHarness();
        h.CollectorRunning();
        for (int p = 0; p < 500; p++)
        {
            h.Processes.Entries.Add(Proc(10_000 + p, 1_000 + p, $"app{p % 50}.exe"));
        }

        for (int c = 0; c < 5_000; c++)
        {
            int pid = 10_000 + (c % 500);
            var local = new IpEndpointValue(IpAddressValue.FromIPv4(192, 168, 1, 2), (ushort)(20_000 + c));
            var remote = new IpEndpointValue(IpAddressValue.FromIPv4(10, (byte)(c / 65_536), (byte)(c / 256 % 256), (byte)(c % 256)), 443);
            h.Connections.Tcp.Add(new TcpConnectionRow(local, remote, TcpState.Established, pid, 0, null));
        }

        for (int u = 0; u < 500; u++)
        {
            h.Connections.Udp.Add(new UdpEndpointRow(new IpEndpointValue(IpAddressValue.FromIPv4(0, 0, 0, 0), (ushort)(40_000 + u)), 10_000 + u, 0, false, null));
        }

        h.Cycle(); // initial snapshot of processes and tables
        var watch = new Stopwatch();
        var worst = TimeSpan.Zero;
        const int cycles = 30;
        for (int i = 0; i < cycles; i++)
        {
            h.FramesForLastSecond(_ => Enumerable.Range(0, 500)
                .Select(p => new ProcessTraffic(new ProcessKey(10_000 + p, 1_000 + p), Bytes(tcpRx: 1_000, udpTx: 100)))
                .ToArray());
            watch.Restart();
            h.Cycle();
            watch.Stop();
            if (watch.Elapsed > worst)
            {
                worst = watch.Elapsed;
            }
        }

        var snapshot = h.Engine.Latest;
        Assert.Equal(5_000, snapshot.Connections.Count(c => c.State == TcpState.Established));
        Assert.Equal(500, snapshot.UdpEndpoints.Count);
        long received = snapshot.Processes.Where(p => p.Key.Pid >= 10_000).Sum(p => p.Totals.TcpReceived);
        Assert.Equal(500L * 1_000 * 4 * cycles, received); // 4 buckets per cycle, nothing lost under load
        Assert.True(worst < TimeSpan.FromMilliseconds(400), $"slowest cycle {worst.TotalMilliseconds:0} ms");
    }
}
