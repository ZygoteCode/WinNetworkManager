using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace WinNetworkManager.Infrastructure.History;

/// <summary>Flags describing the quality of a stored interval.</summary>
[Flags]
public enum SampleFlags
{
    /// <summary>Complete.</summary>
    None = 0,

    /// <summary>The collector was not running: attributed values are unknown (stored as NULL).</summary>
    NoCollector = 1,

    /// <summary>Events were lost: attributed values are a lower bound.</summary>
    Partial = 2,

    /// <summary>Adapter counters unavailable or reset during the interval.</summary>
    NoInterfaceData = 4,
}

/// <summary>System-wide traffic of one interval (bytes, not rates).</summary>
/// <param name="Start">Interval start (UTC).</param>
/// <param name="ResolutionSeconds">Interval length.</param>
/// <param name="PhysicalReceived">Adapter octets received (null when unknown).</param>
/// <param name="PhysicalSent">Adapter octets sent.</param>
/// <param name="TcpReceived">Attributed TCP payload received (external).</param>
/// <param name="TcpSent">Attributed TCP payload sent.</param>
/// <param name="UdpReceived">Attributed UDP payload received.</param>
/// <param name="UdpSent">Attributed UDP payload sent.</param>
/// <param name="LoopbackReceived">Loopback payload received.</param>
/// <param name="LoopbackSent">Loopback payload sent.</param>
/// <param name="Flags">Quality flags.</param>
public sealed record SystemSample(
    DateTimeOffset Start,
    int ResolutionSeconds,
    long? PhysicalReceived,
    long? PhysicalSent,
    long? TcpReceived,
    long? TcpSent,
    long? UdpReceived,
    long? UdpSent,
    long? LoopbackReceived,
    long? LoopbackSent,
    SampleFlags Flags);

/// <summary>Traffic of one application during one minute.</summary>
/// <param name="Start">Minute start (UTC).</param>
/// <param name="AppKey">Application key (normalized executable path or "name:…").</param>
/// <param name="Name">Display name.</param>
/// <param name="Path">Executable path, when known.</param>
/// <param name="TcpReceived">TCP payload received.</param>
/// <param name="TcpSent">TCP payload sent.</param>
/// <param name="UdpReceived">UDP payload received.</param>
/// <param name="UdpSent">UDP payload sent.</param>
public sealed record AppSample(DateTimeOffset Start, string AppKey, string Name, string? Path, long TcpReceived, long TcpSent, long UdpReceived, long UdpSent)
{
    /// <summary>Total bytes.</summary>
    public long Total => TcpReceived + TcpSent + UdpReceived + UdpSent;
}

/// <summary>Traffic of one interface during one minute.</summary>
/// <param name="Start">Minute start (UTC).</param>
/// <param name="InterfaceId">Interface GUID.</param>
/// <param name="Name">Interface name.</param>
/// <param name="Received">Octets received.</param>
/// <param name="Sent">Octets sent.</param>
public sealed record InterfaceSample(DateTimeOffset Start, Guid InterfaceId, string Name, long Received, long Sent);

/// <summary>Aggregate of an application over a time range.</summary>
/// <param name="AppKey">Key.</param>
/// <param name="Name">Name.</param>
/// <param name="Path">Path.</param>
/// <param name="Received">Bytes received.</param>
/// <param name="Sent">Bytes sent.</param>
/// <param name="TcpBytes">TCP bytes (both directions).</param>
/// <param name="UdpBytes">UDP bytes (both directions).</param>
public sealed record AppTotal(string AppKey, string Name, string? Path, long Received, long Sent, long TcpBytes, long UdpBytes)
{
    /// <summary>Total bytes.</summary>
    public long Total => Received + Sent;
}

/// <summary>A batch of rows written in one transaction.</summary>
/// <param name="System">System samples.</param>
/// <param name="Apps">Application minutes.</param>
/// <param name="Interfaces">Interface minutes.</param>
public sealed record HistoryBatch(IReadOnlyList<SystemSample> System, IReadOnlyList<AppSample> Apps, IReadOnlyList<InterfaceSample> Interfaces)
{
    /// <summary>True when nothing to write.</summary>
    public bool IsEmpty => System.Count == 0 && Apps.Count == 0 && Interfaces.Count == 0;
}

/// <summary>Writes history batches (implemented by the SQLite store; replaced in tests).</summary>
public interface IHistorySink
{
    /// <summary>Persists a batch. Rows with an existing key are accumulated, not duplicated.</summary>
    void Write(HistoryBatch batch);
}

/// <summary>
/// Local history in SQLite (WAL). Stores only aggregated byte counts per interval, per application and per
/// interface — never remote addresses or connection details. Thread-safe (one connection, serialized).
/// </summary>
public sealed class SqliteHistoryStore : IHistorySink, IDisposable
{
    /// <summary>Schema version of this build.</summary>
    public const int SchemaVersion = 1;

    private readonly string _path;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, long> _appIds = new(StringComparer.Ordinal);
    private SqliteConnection? _connection;
    private bool _disposed;

    /// <summary>Creates a store for <paramref name="path"/> (opened lazily).</summary>
    public SqliteHistoryStore(string path, ILogger<SqliteHistoryStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _path = path;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Database file size in bytes (0 when missing).</summary>
    public long SizeBytes
    {
        get
        {
            long size = 0;
            foreach (string file in new[] { _path, _path + "-wal" })
            {
                if (File.Exists(file))
                {
                    size += new FileInfo(file).Length;
                }
            }

            return size;
        }
    }

    /// <summary>Opens the database and creates the schema.</summary>
    public void Open()
    {
        lock (_gate)
        {
            EnsureOpen();
        }
    }

    /// <inheritdoc />
    public void Write(HistoryBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            var connection = EnsureOpen();
            using var transaction = connection.BeginTransaction();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO system_samples(ts, resolution, phys_rx, phys_tx, tcp_rx, tcp_tx, udp_rx, udp_tx, loop_rx, loop_tx, flags)
                    VALUES ($ts, $res, $prx, $ptx, $trx, $ttx, $urx, $utx, $lrx, $ltx, $flags)
                    ON CONFLICT(resolution, ts) DO UPDATE SET
                      phys_rx = CASE WHEN phys_rx IS NULL OR excluded.phys_rx IS NULL THEN COALESCE(phys_rx, excluded.phys_rx) ELSE phys_rx + excluded.phys_rx END,
                      phys_tx = CASE WHEN phys_tx IS NULL OR excluded.phys_tx IS NULL THEN COALESCE(phys_tx, excluded.phys_tx) ELSE phys_tx + excluded.phys_tx END,
                      tcp_rx = CASE WHEN tcp_rx IS NULL OR excluded.tcp_rx IS NULL THEN COALESCE(tcp_rx, excluded.tcp_rx) ELSE tcp_rx + excluded.tcp_rx END,
                      tcp_tx = CASE WHEN tcp_tx IS NULL OR excluded.tcp_tx IS NULL THEN COALESCE(tcp_tx, excluded.tcp_tx) ELSE tcp_tx + excluded.tcp_tx END,
                      udp_rx = CASE WHEN udp_rx IS NULL OR excluded.udp_rx IS NULL THEN COALESCE(udp_rx, excluded.udp_rx) ELSE udp_rx + excluded.udp_rx END,
                      udp_tx = CASE WHEN udp_tx IS NULL OR excluded.udp_tx IS NULL THEN COALESCE(udp_tx, excluded.udp_tx) ELSE udp_tx + excluded.udp_tx END,
                      loop_rx = CASE WHEN loop_rx IS NULL OR excluded.loop_rx IS NULL THEN COALESCE(loop_rx, excluded.loop_rx) ELSE loop_rx + excluded.loop_rx END,
                      loop_tx = CASE WHEN loop_tx IS NULL OR excluded.loop_tx IS NULL THEN COALESCE(loop_tx, excluded.loop_tx) ELSE loop_tx + excluded.loop_tx END,
                      flags = flags | excluded.flags
                    """;
                var p = new[] { "$ts", "$res", "$prx", "$ptx", "$trx", "$ttx", "$urx", "$utx", "$lrx", "$ltx", "$flags" }.Select(n => command.Parameters.Add(n, SqliteType.Integer)).ToArray();
                foreach (var s in batch.System)
                {
                    p[0].Value = s.Start.ToUnixTimeSeconds();
                    p[1].Value = s.ResolutionSeconds;
                    p[2].Value = (object?)s.PhysicalReceived ?? DBNull.Value;
                    p[3].Value = (object?)s.PhysicalSent ?? DBNull.Value;
                    p[4].Value = (object?)s.TcpReceived ?? DBNull.Value;
                    p[5].Value = (object?)s.TcpSent ?? DBNull.Value;
                    p[6].Value = (object?)s.UdpReceived ?? DBNull.Value;
                    p[7].Value = (object?)s.UdpSent ?? DBNull.Value;
                    p[8].Value = (object?)s.LoopbackReceived ?? DBNull.Value;
                    p[9].Value = (object?)s.LoopbackSent ?? DBNull.Value;
                    p[10].Value = (int)s.Flags;
                    command.ExecuteNonQuery();
                }
            }

            if (batch.Apps.Count > 0)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO app_samples(ts, app_id, tcp_rx, tcp_tx, udp_rx, udp_tx) VALUES ($ts, $app, $trx, $ttx, $urx, $utx)
                    ON CONFLICT(app_id, ts) DO UPDATE SET tcp_rx = tcp_rx + excluded.tcp_rx, tcp_tx = tcp_tx + excluded.tcp_tx,
                      udp_rx = udp_rx + excluded.udp_rx, udp_tx = udp_tx + excluded.udp_tx
                    """;
                var p = new[] { "$ts", "$app", "$trx", "$ttx", "$urx", "$utx" }.Select(n => command.Parameters.Add(n, SqliteType.Integer)).ToArray();
                foreach (var a in batch.Apps)
                {
                    p[0].Value = a.Start.ToUnixTimeSeconds();
                    p[1].Value = AppId(connection, transaction, a.AppKey, a.Name, a.Path);
                    p[2].Value = a.TcpReceived;
                    p[3].Value = a.TcpSent;
                    p[4].Value = a.UdpReceived;
                    p[5].Value = a.UdpSent;
                    command.ExecuteNonQuery();
                }
            }

            if (batch.Interfaces.Count > 0)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO interface_samples(ts, if_id, name, rx, tx) VALUES ($ts, $if, $name, $rx, $tx)
                    ON CONFLICT(if_id, ts) DO UPDATE SET rx = rx + excluded.rx, tx = tx + excluded.tx, name = excluded.name
                    """;
                var ts = command.Parameters.Add("$ts", SqliteType.Integer);
                var id = command.Parameters.Add("$if", SqliteType.Text);
                var name = command.Parameters.Add("$name", SqliteType.Text);
                var rx = command.Parameters.Add("$rx", SqliteType.Integer);
                var tx = command.Parameters.Add("$tx", SqliteType.Integer);
                foreach (var i in batch.Interfaces)
                {
                    ts.Value = i.Start.ToUnixTimeSeconds();
                    id.Value = i.InterfaceId.ToString("D");
                    name.Value = i.Name;
                    rx.Value = i.Received;
                    tx.Value = i.Sent;
                    command.ExecuteNonQuery();
                }
            }

            transaction.Commit();
        }
    }

    /// <summary>Reads system samples of a resolution in [from, to).</summary>
    public IReadOnlyList<SystemSample> ReadSystem(DateTimeOffset from, DateTimeOffset to, int resolutionSeconds)
    {
        lock (_gate)
        {
            var connection = EnsureOpen();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT ts, phys_rx, phys_tx, tcp_rx, tcp_tx, udp_rx, udp_tx, loop_rx, loop_tx, flags FROM system_samples WHERE resolution = $res AND ts >= $from AND ts < $to ORDER BY ts";
            command.Parameters.AddWithValue("$res", resolutionSeconds);
            command.Parameters.AddWithValue("$from", from.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("$to", to.ToUnixTimeSeconds());
            using var reader = command.ExecuteReader();
            var result = new List<SystemSample>();
            while (reader.Read())
            {
                result.Add(new SystemSample(
                    DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)),
                    resolutionSeconds,
                    Nullable(reader, 1),
                    Nullable(reader, 2),
                    Nullable(reader, 3),
                    Nullable(reader, 4),
                    Nullable(reader, 5),
                    Nullable(reader, 6),
                    Nullable(reader, 7),
                    Nullable(reader, 8),
                    (SampleFlags)reader.GetInt32(9)));
            }

            return result;
        }
    }

    /// <summary>Applications ranked by traffic in [from, to).</summary>
    public IReadOnlyList<AppTotal> ReadTopApps(DateTimeOffset from, DateTimeOffset to, int limit)
    {
        lock (_gate)
        {
            var connection = EnsureOpen();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT a.app_key, a.name, a.path, SUM(s.tcp_rx + s.udp_rx), SUM(s.tcp_tx + s.udp_tx), SUM(s.tcp_rx + s.tcp_tx), SUM(s.udp_rx + s.udp_tx)
                FROM app_samples s JOIN apps a ON a.id = s.app_id
                WHERE s.ts >= $from AND s.ts < $to
                GROUP BY s.app_id
                ORDER BY SUM(s.tcp_rx + s.tcp_tx + s.udp_rx + s.udp_tx) DESC
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$from", from.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("$to", to.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            var result = new List<AppTotal>();
            while (reader.Read())
            {
                result.Add(new AppTotal(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6)));
            }

            return result;
        }
    }

    /// <summary>Per-minute samples of one application in [from, to).</summary>
    public IReadOnlyList<AppSample> ReadApp(string appKey, DateTimeOffset from, DateTimeOffset to)
    {
        lock (_gate)
        {
            var connection = EnsureOpen();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT s.ts, a.app_key, a.name, a.path, s.tcp_rx, s.tcp_tx, s.udp_rx, s.udp_tx
                FROM app_samples s JOIN apps a ON a.id = s.app_id
                WHERE a.app_key = $key AND s.ts >= $from AND s.ts < $to ORDER BY s.ts
                """;
            command.Parameters.AddWithValue("$key", appKey);
            command.Parameters.AddWithValue("$from", from.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("$to", to.ToUnixTimeSeconds());
            using var reader = command.ExecuteReader();
            var result = new List<AppSample>();
            while (reader.Read())
            {
                result.Add(new AppSample(DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7)));
            }

            return result;
        }
    }

    /// <summary>Per-minute samples of interfaces in [from, to).</summary>
    public IReadOnlyList<InterfaceSample> ReadInterfaces(DateTimeOffset from, DateTimeOffset to)
    {
        lock (_gate)
        {
            var connection = EnsureOpen();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT ts, if_id, name, rx, tx FROM interface_samples WHERE ts >= $from AND ts < $to ORDER BY ts";
            command.Parameters.AddWithValue("$from", from.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("$to", to.ToUnixTimeSeconds());
            using var reader = command.ExecuteReader();
            var result = new List<InterfaceSample>();
            while (reader.Read())
            {
                result.Add(new InterfaceSample(DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)), Guid.Parse(reader.GetString(1)), reader.IsDBNull(2) ? string.Empty : reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4)));
            }

            return result;
        }
    }

    /// <summary>Deletes per-second rows before <paramref name="secondsCutoff"/> and per-minute rows before <paramref name="minutesCutoff"/>.</summary>
    public int Prune(DateTimeOffset secondsCutoff, DateTimeOffset minutesCutoff)
    {
        lock (_gate)
        {
            var connection = EnsureOpen();
            int removed = 0;
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM system_samples WHERE (resolution = 1 AND ts < $s) OR (resolution = 60 AND ts < $m)";
            command.Parameters.AddWithValue("$s", secondsCutoff.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("$m", minutesCutoff.ToUnixTimeSeconds());
            removed += command.ExecuteNonQuery();
            command.CommandText = "DELETE FROM app_samples WHERE ts < $m";
            removed += command.ExecuteNonQuery();
            command.CommandText = "DELETE FROM interface_samples WHERE ts < $m";
            removed += command.ExecuteNonQuery();
            command.CommandText = "DELETE FROM apps WHERE id NOT IN (SELECT DISTINCT app_id FROM app_samples)";
            command.ExecuteNonQuery();
            _appIds.Clear();
            return removed;
        }
    }

    /// <summary>Deletes all history.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            var connection = EnsureOpen();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM system_samples; DELETE FROM app_samples; DELETE FROM interface_samples; DELETE FROM apps;";
            command.ExecuteNonQuery();
            command.CommandText = "VACUUM;";
            command.ExecuteNonQuery();
            _appIds.Clear();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _connection?.Dispose();
            _connection = null;
        }

        SqliteConnection.ClearAllPools();
    }

    private SqliteConnection EnsureOpen()
    {
        // Never reopen after Dispose: a write still in flight when the user turns history off must not recreate the file.
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_connection is not null)
        {
            return _connection;
        }

        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA foreign_keys = ON;
                CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS system_samples(
                  ts INTEGER NOT NULL, resolution INTEGER NOT NULL,
                  phys_rx INTEGER, phys_tx INTEGER, tcp_rx INTEGER, tcp_tx INTEGER, udp_rx INTEGER, udp_tx INTEGER, loop_rx INTEGER, loop_tx INTEGER,
                  flags INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(resolution, ts)) WITHOUT ROWID;
                CREATE TABLE IF NOT EXISTS apps(id INTEGER PRIMARY KEY, app_key TEXT NOT NULL UNIQUE, name TEXT NOT NULL, path TEXT);
                CREATE TABLE IF NOT EXISTS app_samples(
                  ts INTEGER NOT NULL, app_id INTEGER NOT NULL REFERENCES apps(id),
                  tcp_rx INTEGER NOT NULL, tcp_tx INTEGER NOT NULL, udp_rx INTEGER NOT NULL, udp_tx INTEGER NOT NULL,
                  PRIMARY KEY(app_id, ts)) WITHOUT ROWID;
                CREATE INDEX IF NOT EXISTS ix_app_samples_ts ON app_samples(ts);
                CREATE TABLE IF NOT EXISTS interface_samples(
                  ts INTEGER NOT NULL, if_id TEXT NOT NULL, name TEXT, rx INTEGER NOT NULL, tx INTEGER NOT NULL,
                  PRIMARY KEY(if_id, ts)) WITHOUT ROWID;
                CREATE INDEX IF NOT EXISTS ix_interface_samples_ts ON interface_samples(ts);
                """;
            command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO meta(key, value) VALUES ('schema', $v) ON CONFLICT(key) DO NOTHING";
            command.Parameters.AddWithValue("$v", SchemaVersion.ToString(CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        _logger.LogInformation("History database opened at {Path}.", _path);
        _connection = connection;
        return connection;
    }

    private long AppId(SqliteConnection connection, SqliteTransaction transaction, string key, string name, string? path)
    {
        if (_appIds.TryGetValue(key, out long id))
        {
            return id;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO apps(app_key, name, path) VALUES ($key, $name, $path)
            ON CONFLICT(app_key) DO UPDATE SET name = excluded.name, path = COALESCE(excluded.path, path)
            RETURNING id
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$path", (object?)path ?? DBNull.Value);
        id = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        _appIds[key] = id;
        return id;
    }

    private static long? Nullable(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
}
