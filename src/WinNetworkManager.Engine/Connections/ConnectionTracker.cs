using System.Numerics;
using WinNetworkManager.Core.Networking;
using WinNetworkManager.Core.Processes;
using WinNetworkManager.Core.Traffic;
using WinNetworkManager.Engine.Model;
using WinNetworkManager.Engine.Processes;
using WinNetworkManager.Platform.Windows.IpHelper;
using WinNetworkManager.Telemetry.Model;

namespace WinNetworkManager.Engine.Connections;

/// <summary>Received/sent byte pair.</summary>
internal struct DirectionBytes : IAdditionOperators<DirectionBytes, DirectionBytes, DirectionBytes>
{
    public long Received;
    public long Sent;

    public DirectionBytes(long received, long sent)
    {
        Received = received;
        Sent = sent;
    }

    public static DirectionBytes operator +(DirectionBytes left, DirectionBytes right) => new(left.Received + right.Received, left.Sent + right.Sent);
}

/// <summary>Socket counts per process.</summary>
internal struct SocketCounts
{
    public int Tcp;
    public int Listeners;
    public int Udp;
    public HashSet<IpAddressValue>? Remotes;
}

/// <summary>Shared state of TCP and UDP records.</summary>
internal abstract class SocketRecord
{
    private BucketRing<DirectionBytes>? _buckets;

    public long Id { get; init; }

    public IpEndpointValue Local { get; init; }

    public int OwnerPid { get; set; }

    public ProcessRecord? Owner { get; set; }

    public string? OwnerModule { get; set; }

    public DateTimeOffset FirstSeen { get; init; }

    public DateTimeOffset? CreatedAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    public bool SeenThisPoll { get; set; }

    public long Received { get; private set; }

    public long Sent { get; private set; }

    public long Retransmissions { get; set; }

    public bool BytesAvailable { get; set; }

    public long LastBytesBucket { get; private set; } = -1;

    public long Version { get; set; }

    public ConnectionOrigin Origin { get; init; }

    public void AddBytes(long bucket, long received, long sent)
    {
        _buckets ??= new BucketRing<DirectionBytes>(64);
        _buckets.Add(bucket, new DirectionBytes(received, sent));
        Received += received;
        Sent += sent;
        LastBytesBucket = Math.Max(LastBytesBucket, bucket);
        Version++;
    }

    public DirectionBytes Window(long endInclusive, int count) => _buckets?.Sum(endInclusive - count + 1, endInclusive + 1) ?? default;
}

internal sealed class TcpRecord : SocketRecord
{
    public IpEndpointValue Remote { get; init; }

    public TcpState State { get; set; }

    public ConnectionView? Cached { get; set; }

    public long CachedVersion { get; set; } = -1;
}

internal sealed class UdpRecord : SocketRecord
{
    public Dictionary<IpEndpointValue, UdpPeerState> Peers { get; } = [];

    public int PeerCount { get; set; }

    public UdpEndpointView? Cached { get; set; }

    public long CachedVersion { get; set; } = -1;
}

internal sealed class UdpPeerState
{
    public long Received;
    public long Sent;
    public DateTimeOffset LastSeen;
}

/// <summary>
/// Tracks TCP connections/listeners and UDP endpoints from IP Helper polls, keeps recently closed ones visible for a
/// while, and joins collector flows to them for per-connection bytes and observed UDP peers. Engine-thread only.
/// </summary>
internal sealed class ConnectionTracker
{
    private const int MaxPeersPerEndpoint = 256;
    private readonly Dictionary<(IpEndpointValue Local, IpEndpointValue Remote), TcpRecord> _tcp = [];
    private readonly Dictionary<(IpEndpointValue Local, int Pid), TcpRecord> _listeners = [];
    private readonly Dictionary<(IpEndpointValue Local, int Pid), UdpRecord> _udp = [];
    private readonly List<TcpConnectionRow> _tcpRows = [];
    private readonly List<UdpEndpointRow> _udpRows = [];
    private long _nextId;

    /// <summary>TCP records (connections, listeners, recently closed).</summary>
    public IEnumerable<TcpRecord> TcpRecords => _tcp.Values.Concat(_listeners.Values);

    /// <summary>UDP records.</summary>
    public IEnumerable<UdpRecord> UdpRecords => _udp.Values;

    /// <summary>Polls the tables and reconciles records.</summary>
    public void Poll(IConnectionTableSource source, ProcessCatalog catalog, DateTimeOffset now, bool collectorActive)
    {
        ArgumentNullException.ThrowIfNull(source);
        source.ReadTcp(_tcpRows);
        source.ReadUdp(_udpRows);

        foreach (var record in TcpRecords)
        {
            record.SeenThisPoll = false;
        }

        foreach (var record in _udp.Values)
        {
            record.SeenThisPoll = false;
        }

        foreach (var row in _tcpRows)
        {
            var local = Normalize(row.Local);
            TcpRecord? record;
            bool isNew;
            if (row.State == TcpState.Listen)
            {
                var key = (local, row.OwningPid);
                isNew = !_listeners.TryGetValue(key, out record);
                if (isNew)
                {
                    record = NewTcp(local, Normalize(row.Remote), row, catalog, now, collectorActive);
                    _listeners[key] = record;
                }
            }
            else
            {
                var key = (local, Normalize(row.Remote));
                isNew = !_tcp.TryGetValue(key, out record);
                if (isNew || record!.ClosedAt is not null)
                {
                    // A closed record with the same 4-tuple is a new connection reusing the ports.
                    record = NewTcp(local, key.Item2, row, catalog, now, collectorActive);
                    _tcp[key] = record;
                }
            }

            UpdateTcp(record!, row, catalog, now);
            record!.SeenThisPoll = true;
        }

        foreach (var row in _udpRows)
        {
            var key = (Normalize(row.Local), row.OwningPid);
            if (!_udp.TryGetValue(key, out var record) || record.ClosedAt is not null)
            {
                record = new UdpRecord
                {
                    Id = ++_nextId,
                    Local = key.Item1,
                    OwnerPid = row.OwningPid,
                    Owner = row.OwningPid == 0 ? null : catalog.FindAlive(row.OwningPid) ?? catalog.ResolveForTraffic(new ProcessKey(row.OwningPid, 0), now),
                    FirstSeen = now,
                    CreatedAt = Plausible(row.CreateTimestamp, now),
                    OwnerModule = row.OwnerModule,
                    BytesAvailable = collectorActive,
                    Origin = ConnectionOrigin.Table,
                };
                _udp[key] = record;
            }

            if (row.OwnerModule is not null && record.OwnerModule is null)
            {
                record.OwnerModule = row.OwnerModule;
                record.Version++;
            }

            record.SeenThisPoll = true;
        }

        CloseUnseen(_tcp.Values, now);
        CloseUnseen(_listeners.Values, now);
        CloseUnseen(_udp.Values, now);
    }

    /// <summary>Joins one collector flow to its socket record.</summary>
    public void ApplyFlow(in FlowTraffic flow, long bucket, ProcessRecord owner, DateTimeOffset when)
    {
        var key = flow.Key;
        if (key.Protocol == TransportProtocol.Tcp)
        {
            if (!_tcp.TryGetValue((key.Local, key.Remote), out var record))
            {
                // Opened and closed between two polls: visible only through ETW.
                record = new TcpRecord
                {
                    Id = ++_nextId,
                    Local = key.Local,
                    Remote = key.Remote,
                    State = TcpState.Closed,
                    OwnerPid = key.Process.Pid,
                    Owner = owner,
                    FirstSeen = when,
                    ClosedAt = when,
                    BytesAvailable = true,
                    Origin = ConnectionOrigin.ObservedOnly,
                };
                _tcp[(key.Local, key.Remote)] = record;
            }

            record.AddBytes(bucket, flow.Counters.ReceivedBytes, flow.Counters.SentBytes);
            record.Retransmissions += flow.Counters.Retransmissions;
            return;
        }

        var udp = FindUdp(key.Local, key.Process.Pid);
        if (udp is null)
        {
            udp = new UdpRecord
            {
                Id = ++_nextId,
                Local = key.Local,
                OwnerPid = key.Process.Pid,
                Owner = owner,
                FirstSeen = when,
                ClosedAt = when,
                BytesAvailable = true,
                Origin = ConnectionOrigin.ObservedOnly,
            };
            _udp[(key.Local, key.Process.Pid)] = udp;
        }

        udp.AddBytes(bucket, flow.Counters.ReceivedBytes, flow.Counters.SentBytes);
        if (!udp.Peers.TryGetValue(key.Remote, out var peer))
        {
            if (udp.Peers.Count >= MaxPeersPerEndpoint)
            {
                var oldest = udp.Peers.MinBy(p => p.Value.LastSeen);
                udp.Peers.Remove(oldest.Key);
            }

            peer = new UdpPeerState();
            udp.Peers[key.Remote] = peer;
            udp.PeerCount++;
        }

        peer.Received += flow.Counters.ReceivedBytes;
        peer.Sent += flow.Counters.SentBytes;
        peer.LastSeen = when;
    }

    /// <summary>Removes closed records older than <paramref name="retention"/>.</summary>
    public void Prune(DateTimeOffset now, TimeSpan retention)
    {
        Remove(_tcp, now, retention);
        Remove(_listeners, now, retention);
        Remove(_udp, now, retention);
    }

    /// <summary>Socket counts per owning process (live sockets only).</summary>
    public Dictionary<ProcessRecord, SocketCounts> CountByProcess()
    {
        var counts = new Dictionary<ProcessRecord, SocketCounts>(ReferenceEqualityComparer.Instance);
        foreach (var record in TcpRecords)
        {
            if (record.ClosedAt is not null || record.Owner is null)
            {
                continue;
            }

            ref var c = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(counts, record.Owner, out _);
            if (record.State == TcpState.Listen)
            {
                c.Listeners++;
            }
            else if (TcpStateInfo.IsActive(record.State))
            {
                c.Tcp++;
                (c.Remotes ??= []).Add(record.Remote.Address);
            }
        }

        foreach (var record in _udp.Values)
        {
            if (record.ClosedAt is not null || record.Owner is null)
            {
                continue;
            }

            ref var c = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(counts, record.Owner, out _);
            c.Udp++;
            foreach (var peer in record.Peers.Keys)
            {
                (c.Remotes ??= []).Add(peer.Address);
            }
        }

        return counts;
    }

    internal static IpEndpointValue Normalize(IpEndpointValue endpoint) =>
        new(endpoint.Address.WithoutScope(), endpoint.Port);

    internal static DateTimeOffset? Plausible(long fileTime, DateTimeOffset now)
    {
        if (fileTime <= 0)
        {
            return null;
        }

        try
        {
            var value = new DateTimeOffset(DateTime.FromFileTimeUtc(fileTime), TimeSpan.Zero);
            return value.Year >= 2000 && value <= now.AddDays(1) ? value : null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private TcpRecord NewTcp(IpEndpointValue local, IpEndpointValue remote, in TcpConnectionRow row, ProcessCatalog catalog, DateTimeOffset now, bool collectorActive) => new()
    {
        Id = ++_nextId,
        Local = local,
        Remote = remote,
        State = row.State,
        OwnerPid = row.OwningPid,
        Owner = row.OwningPid == 0 ? null : catalog.FindAlive(row.OwningPid) ?? catalog.ResolveForTraffic(new ProcessKey(row.OwningPid, 0), now),
        FirstSeen = now,
        CreatedAt = Plausible(row.CreateTimestamp, now),
        OwnerModule = row.OwnerModule,
        BytesAvailable = collectorActive,
        Origin = ConnectionOrigin.Table,
    };

    private static void UpdateTcp(TcpRecord record, in TcpConnectionRow row, ProcessCatalog catalog, DateTimeOffset now)
    {
        if (record.State != row.State)
        {
            record.State = row.State;
            record.Version++;
        }

        if (row.OwningPid != record.OwnerPid)
        {
            // TIME_WAIT rows report PID 0: keep the last known owner for context, but expose the reported PID.
            if (row.OwningPid != 0)
            {
                record.Owner = catalog.FindAlive(row.OwningPid) ?? catalog.ResolveForTraffic(new ProcessKey(row.OwningPid, 0), now);
            }

            record.OwnerPid = row.OwningPid;
            record.Version++;
        }

        if (record.CreatedAt is null && Plausible(row.CreateTimestamp, now) is { } created)
        {
            record.CreatedAt = created;
            record.Version++;
        }

        if (row.OwnerModule is not null && record.OwnerModule is null)
        {
            record.OwnerModule = row.OwnerModule;
            record.Version++;
        }
    }

    private UdpRecord? FindUdp(IpEndpointValue local, int pid)
    {
        if (_udp.TryGetValue((local, pid), out var exact))
        {
            return exact;
        }

        var wildcard = new IpEndpointValue(local.Address.IsIPv4 ? IpAddressValue.IPv4Any : IpAddressValue.IPv6Any, local.Port);
        if (_udp.TryGetValue((wildcard, pid), out var any))
        {
            return any;
        }

        // Dual-stack sockets bound to [::] receive IPv4 traffic.
        if (local.Address.IsIPv4 && _udp.TryGetValue((new IpEndpointValue(IpAddressValue.IPv6Any, local.Port), pid), out var dual))
        {
            return dual;
        }

        return null;
    }

    private static void CloseUnseen<T>(IEnumerable<T> records, DateTimeOffset now)
        where T : SocketRecord
    {
        foreach (var record in records)
        {
            if (!record.SeenThisPoll && record.ClosedAt is null && record.Origin == ConnectionOrigin.Table)
            {
                record.ClosedAt = now;
                if (record is TcpRecord tcp)
                {
                    tcp.State = TcpState.Closed;
                }

                record.Version++;
            }
        }
    }

    private static void Remove<TKey, T>(Dictionary<TKey, T> records, DateTimeOffset now, TimeSpan retention)
        where TKey : notnull
        where T : SocketRecord
    {
        List<TKey>? stale = null;
        foreach (var (key, record) in records)
        {
            if (record.ClosedAt is { } closed && now - closed > retention)
            {
                (stale ??= []).Add(key);
            }
        }

        if (stale is not null)
        {
            foreach (var key in stale)
            {
                records.Remove(key);
            }
        }
    }
}
