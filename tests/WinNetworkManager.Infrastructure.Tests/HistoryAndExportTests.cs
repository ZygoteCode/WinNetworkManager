using System.Text;
using System.Text.Json;
using WinNetworkManager.Infrastructure.Export;
using WinNetworkManager.Infrastructure.History;

namespace WinNetworkManager.Infrastructure.Tests;

public sealed class HistoryAndExportTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 21, 10, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wnm-history-" + Guid.NewGuid().ToString("N"));

    public HistoryAndExportTests() => Directory.CreateDirectory(_dir);

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

    private SqliteHistoryStore Store() => new(Path.Combine(_dir, "history.db"));

    private static SystemSample Sys(int second, long? rx, long? tcp, SampleFlags flags = SampleFlags.None, int resolution = 1) =>
        new(T0.AddSeconds(second), resolution, rx, rx / 2, tcp, tcp / 2, 10, 5, 1, 1, flags);

    [Fact]
    public void System_samples_round_trip_and_accumulate_on_conflict()
    {
        using var store = Store();
        store.Write(new HistoryBatch([Sys(0, 1000, 800), Sys(1, 2000, null, SampleFlags.NoCollector)], [], []));
        store.Write(new HistoryBatch([Sys(0, 500, 200, SampleFlags.Partial)], [], [])); // same second again (clock re-anchor)

        var rows = store.ReadSystem(T0, T0.AddSeconds(10), 1);
        Assert.Equal(2, rows.Count);
        Assert.Equal(1500, rows[0].PhysicalReceived);
        Assert.Equal(1000, rows[0].TcpReceived);
        Assert.Equal(SampleFlags.Partial, rows[0].Flags);
        Assert.Null(rows[1].TcpReceived); // unknown, never zero
        Assert.Equal(SampleFlags.NoCollector, rows[1].Flags);
        Assert.Empty(store.ReadSystem(T0, T0.AddSeconds(10), 60));
    }

    [Fact]
    public void Apps_are_ranked_and_series_are_returned()
    {
        using var store = Store();
        store.Write(new HistoryBatch([], [
            new AppSample(T0, "c:\\a.exe", "a.exe", "C:\\a.exe", 100, 50, 0, 0),
            new AppSample(T0.AddMinutes(1), "c:\\a.exe", "a.exe", "C:\\a.exe", 100, 50, 10, 0),
            new AppSample(T0, "name:b.exe", "b.exe", null, 1000, 0, 0, 0),
        ], []));
        store.Write(new HistoryBatch([], [new AppSample(T0, "c:\\a.exe", "a.exe", "C:\\a.exe", 1, 1, 1, 1)], [])); // accumulates

        var top = store.ReadTopApps(T0, T0.AddHours(1), 10);
        Assert.Equal(["name:b.exe", "c:\\a.exe"], top.Select(t => t.AppKey));
        Assert.Equal(1000, top[0].Total);
        Assert.Equal(314, top[1].Total);
        Assert.Equal(10 + 1 + 1, top[1].UdpBytes);

        var series = store.ReadApp("c:\\a.exe", T0, T0.AddHours(1));
        Assert.Equal(2, series.Count);
        Assert.Equal(101, series[0].TcpReceived);
    }

    [Fact]
    public void Interfaces_prune_and_clear()
    {
        using var store = Store();
        var nic = Guid.NewGuid();
        store.Write(new HistoryBatch(
            [Sys(0, 1, 1), Sys(0, 1, 1, resolution: 60)],
            [new AppSample(T0, "k", "n", null, 1, 1, 1, 1)],
            [new InterfaceSample(T0, nic, "Ethernet", 10, 20), new InterfaceSample(T0.AddDays(2), nic, "Ethernet", 1, 2)]));
        Assert.Equal(2, store.ReadInterfaces(T0, T0.AddDays(3)).Count);

        int removed = store.Prune(T0.AddSeconds(1), T0.AddDays(1));
        Assert.True(removed >= 4);
        Assert.Empty(store.ReadSystem(T0, T0.AddDays(3), 1));
        Assert.Empty(store.ReadTopApps(T0, T0.AddDays(3), 10));
        Assert.Single(store.ReadInterfaces(T0, T0.AddDays(3)));

        store.Clear();
        Assert.Empty(store.ReadInterfaces(T0, T0.AddDays(3)));
        Assert.True(store.SizeBytes > 0);
    }

    [Fact]
    public void Store_reopens_existing_database()
    {
        using (var store = Store())
        {
            store.Write(new HistoryBatch([Sys(5, 42, 42)], [], []));
        }

        using var again = Store();
        Assert.Equal(42, again.ReadSystem(T0, T0.AddMinutes(1), 1).Single().PhysicalReceived);
    }

    private static ExportTable SampleTable() => new(
        "Test",
        [new ExportColumn("name", "Name"), new ExportColumn("bytes", "Bytes"), new ExportColumn("rate_bytes_per_s", "Rate"), new ExportColumn("when_utc", "Time"), new ExportColumn("ok", "Flag")],
        [
            ["a,b", 1234567L, 1.5, T0, true],
            ["=cmd|' /C calc'!A0", null, double.NaN, null, false],
            ["quote\"inside", -5, 0.125, T0.ToOffset(TimeSpan.FromHours(2)), true],
        ],
        new Dictionary<string, string> { ["units"] = "bytes", ["source"] = "test" });

    [Fact]
    public void Csv_export_is_invariant_escaped_and_neutralizes_formulas()
    {
        using var writer = new StringWriter();
        TableExporter.WriteCsv(writer, SampleTable());
        string[] lines = writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("# Test", lines[0]);
        Assert.Contains("# units: bytes", lines);
        Assert.Contains("name,bytes,rate_bytes_per_s,when_utc,ok", lines);
        Assert.Contains("\"a,b\",1234567,1.5,2026-11-21T10:00:00.0000000+00:00,true", lines);
        Assert.Contains("\"'=cmd|' /C calc'!A0\",,,,false", lines);
        Assert.Contains("\"quote\"\"inside\",-5,0.125,2026-11-21T10:00:00.0000000+00:00,true", lines);
    }

    [Fact]
    public void Json_export_keeps_types_and_metadata()
    {
        using var stream = new MemoryStream();
        TableExporter.WriteJson(stream, SampleTable());
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(stream.ToArray()));
        var root = doc.RootElement;
        Assert.Equal("bytes", root.GetProperty("metadata").GetProperty("units").GetString());
        var first = root.GetProperty("rows")[0];
        Assert.Equal(JsonValueKind.Number, first.GetProperty("bytes").ValueKind);
        Assert.Equal(1234567, first.GetProperty("bytes").GetInt64());
        Assert.True(first.GetProperty("ok").GetBoolean());
        Assert.Equal("2026-11-21T10:00:00.0000000+00:00", first.GetProperty("when_utc").GetString());
        var second = root.GetProperty("rows")[1];
        Assert.Equal(JsonValueKind.Null, second.GetProperty("bytes").ValueKind);
        Assert.Equal(JsonValueKind.Null, second.GetProperty("rate_bytes_per_s").ValueKind);
        Assert.Equal(5, root.GetProperty("columns").GetArrayLength());
    }
}
