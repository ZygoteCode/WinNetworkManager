using WinNetworkManager.Core.Processes;
using WinNetworkManager.Infrastructure.History;
using WinNetworkManager.Telemetry.Model;
using static WinNetworkManager.Engine.Tests.EngineHarness;

namespace WinNetworkManager.Engine.Tests;

public sealed class HistoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wnm-enginehistory-" + Guid.NewGuid().ToString("N"));

    public HistoryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Per_second_and_per_minute_history_is_recorded_with_exact_bytes()
    {
        await using var h = new EngineHarness();
        using var store = new SqliteHistoryStore(Path.Combine(_dir, "h.db"));
        h.Engine.AttachHistoryStore(store);
        h.CollectorRunning();
        h.Processes.Entries.Add(Proc(42, 7, "app.exe"));
        var nic = Guid.NewGuid();
        ulong rx = 0;
        h.Interfaces.Rows.Add(Nic(1, nic, rx, 0));
        h.Cycle();

        // 130 seconds of steady traffic: 1 000 B/s attributed (4 × 250 B buckets), 2 000 B/s on the adapter.
        for (int i = 0; i < 130; i++)
        {
            long end = h.CurrentBucket - 4;
            for (long b = end - 3; b <= end; b++)
            {
                h.Frame(b, [new ProcessTraffic(new ProcessKey(42, 7), Bytes(tcpRx: 250))]);
            }

            rx += 2_000;
            h.Interfaces.Rows[0] = Nic(1, nic, rx, 0);
            h.Cycle();
        }

        var recent = await h.Engine.GetRecentSystemSamplesAsync(TimeSpan.FromSeconds(60));
        Assert.InRange(recent.Count, 55, 61);
        var steady = recent.Where(s => s.TcpReceived is not null && s.PhysicalReceived is not null).ToList();
        Assert.NotEmpty(steady);
        Assert.All(steady, s => Assert.Equal(1_000, s.TcpReceived));
        Assert.All(steady, s => Assert.Equal(2_000, s.PhysicalReceived));

        await Task.Delay(500, TestContext.Current.CancellationToken); // background writer
        var minutes = store.ReadSystem(DateTimeOffset.MinValue.AddYears(2000), DateTimeOffset.MaxValue.AddYears(-1), 60);
        Assert.NotEmpty(minutes);
        var full = minutes.Where(m => m.TcpReceived == 60_000).ToList();
        Assert.NotEmpty(full);
        var apps = store.ReadTopApps(DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow.AddYears(5), 5);
        Assert.Equal("app.exe", Assert.Single(apps).Name);
        Assert.True(store.ReadInterfaces(DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow.AddYears(5)).Count >= 1);
    }

    [Fact]
    public async Task Stopping_the_engine_keeps_the_partial_current_minute()
    {
        using var store = new SqliteHistoryStore(Path.Combine(_dir, "partial.db"));
        await using (var h = new EngineHarness())
        {
            h.Engine.AttachHistoryStore(store);
            h.CollectorRunning();
            h.Processes.Entries.Add(Proc(42, 7, "app.exe"));
            h.Cycle();
            for (int i = 0; i < 10; i++)
            {
                long end = h.CurrentBucket - 4;
                for (long b = end - 3; b <= end; b++)
                {
                    h.Frame(b, [new ProcessTraffic(new ProcessKey(42, 7), Bytes(tcpRx: 250))]);
                }

                h.Cycle();
            }
        }

        // Disposing the engine (application exit) wrote the minute still in progress: every byte is on disk.
        var app = Assert.Single(store.ReadTopApps(DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow.AddYears(5), 5));
        Assert.Equal("app.exe", app.Name);
        Assert.Equal(10_000, app.Received);
    }

    [Fact]
    public async Task A_failing_database_never_stops_monitoring()
    {
        await using var h = new EngineHarness();
        var store = new SqliteHistoryStore(Path.Combine(_dir, "broken.db"));
        store.Dispose(); // every write now fails
        h.Engine.AttachHistoryStore(store);
        var nic = Guid.NewGuid();
        h.Interfaces.Rows.Add(Nic(1, nic, 0, 0));
        var sequences = new List<long>();
        for (int i = 0; i < 20; i++)
        {
            h.Interfaces.Rows[0] = Nic(1, nic, (ulong)(i + 1) * 1_000, 0);
            sequences.Add(h.Cycle().Sequence);
        }

        await Task.Delay(500, TestContext.Current.CancellationToken); // background writer
        Assert.Equal(Enumerable.Range(0, 20).Select(i => sequences[0] + i), sequences); // every cycle published a snapshot
        Assert.True(h.Engine.DroppedHistoryBatches > 0);
        Assert.NotEmpty(await h.Engine.GetRecentSystemSamplesAsync(TimeSpan.FromMinutes(1))); // in-memory history still works
    }

    [Fact]
    public async Task Seconds_without_collector_are_marked_unknown_not_zero()
    {
        await using var h = new EngineHarness();
        var nic = Guid.NewGuid();
        h.Interfaces.Rows.Add(Nic(1, nic, 0, 0));
        h.Cycle();
        for (int i = 0; i < 10; i++)
        {
            h.Interfaces.Rows[0] = Nic(1, nic, (ulong)(i + 1) * 100, 0);
            h.Cycle();
        }

        var recent = await h.Engine.GetRecentSystemSamplesAsync(TimeSpan.FromMinutes(1));
        Assert.NotEmpty(recent);
        Assert.All(recent, s => Assert.Null(s.TcpReceived));
        Assert.All(recent, s => Assert.True(s.Flags.HasFlag(SampleFlags.NoCollector)));
    }
}
