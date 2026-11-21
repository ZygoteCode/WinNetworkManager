using System.Runtime.InteropServices;
using WinNetworkManager.Core.Networking;
using WinNetworkManager.Core.Processes;
using WinNetworkManager.Core.Time;
using WinNetworkManager.Core.Traffic;
using WinNetworkManager.Telemetry.Model;

namespace WinNetworkManager.Telemetry.Collection;

/// <summary>
/// Aggregates events into fixed buckets of the QPC timeline by <em>event</em> timestamp (ETW delivers events in
/// batches, up to ~0.5 s late with a 200 ms flush timer), and emits a frame for every bucket, in order, once
/// <c>bucketEnd + finalizationDelay</c> has passed. Events older than the oldest open bucket are added to it and
/// counted as late, so totals are never lost. Thread-safe: events arrive on the consumer thread, finalization runs on
/// the collector loop.
/// </summary>
internal sealed class TrafficBucketer
{
    private readonly BucketTimeline _timeline;
    private readonly long _delayTicks;
    private readonly int _maxFlows;
    private readonly Lock _gate = new();
    private readonly Bucket[] _ring;
    private long _oldestOpen = -1;
    private long _newestOpen = -1;
    private long _pendingLost;

    public TrafficBucketer(BucketTimeline timeline, TimeSpan finalizationDelay, int maxFlowsPerBucket, int ringCapacity = 256)
    {
        _timeline = timeline;
        _delayTicks = timeline.ToTicks(finalizationDelay);
        _maxFlows = maxFlowsPerBucket;
        _ring = new Bucket[ringCapacity];
        for (int i = 0; i < ringCapacity; i++)
        {
            _ring[i] = new Bucket();
        }
    }

    /// <summary>True once <see cref="Start"/> was called.</summary>
    public bool IsStarted
    {
        get
        {
            lock (_gate)
            {
                return _oldestOpen >= 0;
            }
        }
    }

    /// <summary>Starts (or restarts after an outage) at the bucket containing <paramref name="nowTimestamp"/>.</summary>
    public void Start(long nowTimestamp)
    {
        lock (_gate)
        {
            foreach (var bucket in _ring)
            {
                bucket.Reset(-1);
            }

            _oldestOpen = _timeline.IndexOf(nowTimestamp);
            _newestOpen = _oldestOpen - 1;
            _pendingLost = 0;
        }
    }

    /// <summary>Accounts one decoded network event.</summary>
    public void AddNetworkEvent(in NetworkEvent ev, ProcessKey process, bool loopback)
    {
        lock (_gate)
        {
            var bucket = BucketFor(ev.Timestamp);
            if (bucket is null)
            {
                return;
            }

            bucket.Events++;
            if (ev.Kind is not (NetworkEventKind.TcpReceive or NetworkEventKind.TcpSend or NetworkEventKind.UdpReceive
                or NetworkEventKind.UdpSend or NetworkEventKind.TcpRetransmit))
            {
                return; // connect/accept/disconnect/copy carry no countable bytes
            }

            long size = ev.Size;
            bool carriesBytes = true;
            ref TrafficCounters counters = ref CollectionsMarshal.GetValueRefOrAddDefault(bucket.Processes, process, out _);
            ref ProtocolBytes scope = ref loopback ? ref counters.Loopback : ref counters.External;
            switch (ev.Kind)
            {
                case NetworkEventKind.TcpReceive:
                    scope.TcpReceived += size;
                    break;
                case NetworkEventKind.TcpSend:
                    scope.TcpSent += size;
                    break;
                case NetworkEventKind.UdpReceive:
                    scope.UdpReceived += size;
                    break;
                case NetworkEventKind.UdpSend:
                    scope.UdpSent += size;
                    break;
                case NetworkEventKind.TcpRetransmit:
                    counters.TcpRetransmittedBytes += size;
                    counters.TcpRetransmissions++;
                    break;
                default:
                    carriesBytes = false;
                    break;
            }

            if (!carriesBytes || ev.Remote.IsEmpty)
            {
                return;
            }

            var key = new FlowKey(
                ev.Protocol,
                process,
                new IpEndpointValue(ev.Local.Address.WithoutScope(), ev.Local.Port),
                new IpEndpointValue(ev.Remote.Address.WithoutScope(), ev.Remote.Port));
            if (bucket.Flows.Count >= _maxFlows && !bucket.Flows.ContainsKey(key))
            {
                bucket.DroppedFlows++;
                return;
            }

            ref FlowCounters flow = ref CollectionsMarshal.GetValueRefOrAddDefault(bucket.Flows, key, out _);
            switch (ev.Kind)
            {
                case NetworkEventKind.TcpReceive or NetworkEventKind.UdpReceive:
                    flow.ReceivedBytes += size;
                    flow.ReceiveEvents++;
                    break;
                case NetworkEventKind.TcpSend or NetworkEventKind.UdpSend:
                    flow.SentBytes += size;
                    flow.SendEvents++;
                    break;
                case NetworkEventKind.TcpRetransmit:
                    flow.RetransmittedBytes += size;
                    flow.Retransmissions++;
                    break;
            }
        }
    }

    /// <summary>Counts an event that carries no bytes (connect/accept/disconnect/copy).</summary>
    public void CountEvent(long timestamp)
    {
        lock (_gate)
        {
            var bucket = BucketFor(timestamp);
            if (bucket is not null)
            {
                bucket.Events++;
            }
        }
    }

    /// <summary>Records a process start/exit.</summary>
    public void AddProcessEvent(ProcessLifecycleEvent ev)
    {
        lock (_gate)
        {
            BucketFor(ev.Timestamp)?.ProcessEvents.Add(ev);
        }
    }

    /// <summary>Records an event that could not be decoded.</summary>
    public void AddDecodeFailure(long timestamp)
    {
        lock (_gate)
        {
            var bucket = BucketFor(timestamp);
            if (bucket is not null)
            {
                bucket.DecodeFailures++;
            }
        }
    }

    /// <summary>Records events reported lost by ETW; attached to the next finalized frame.</summary>
    public void AddLostEvents(long count)
    {
        if (count <= 0)
        {
            return;
        }

        lock (_gate)
        {
            _pendingLost += count;
        }
    }

    /// <summary>Finalizes every bucket whose end + delay is not after <paramref name="nowTimestamp"/>.</summary>
    public List<TelemetryFrame> Finalize(long nowTimestamp) => FinalizeThrough(_timeline.IndexOf(nowTimestamp - _delayTicks) - 1);

    /// <summary>Finalizes everything up to and including the bucket of <paramref name="nowTimestamp"/> (shutdown).</summary>
    public List<TelemetryFrame> FinalizeAll(long nowTimestamp) => FinalizeThrough(Math.Max(_timeline.IndexOf(nowTimestamp), _newestOpen));

    private List<TelemetryFrame> FinalizeThrough(long lastIndex)
    {
        var frames = new List<TelemetryFrame>();
        lock (_gate)
        {
            if (_oldestOpen < 0)
            {
                return frames;
            }

            // After a long stall (suspend/resume, debugger) do not emit thousands of empty frames: only buckets that
            // hold data are emitted and consumers see a jump in BucketIndex, i.e. a gap without measurements.
            bool sparse = lastIndex - _oldestOpen + 1 > _ring.Length;
            for (long index = _oldestOpen; index <= lastIndex; index++)
            {
                var bucket = _ring[Slot(index)];
                bool hasData = bucket.Index == index;
                if (sparse && !hasData && index < lastIndex - 3)
                {
                    continue;
                }

                long lost = _pendingLost;
                _pendingLost = 0;
                frames.Add(new TelemetryFrame
                {
                    BucketIndex = index,
                    StartTimestamp = _timeline.StartOf(index),
                    EndTimestamp = _timeline.EndOf(index),
                    Processes = hasData ? bucket.Processes.Select(kv => new ProcessTraffic(kv.Key, kv.Value)).ToArray() : [],
                    Flows = hasData ? bucket.Flows.Select(kv => new FlowTraffic(kv.Key, kv.Value)).ToArray() : [],
                    ProcessEvents = hasData ? bucket.ProcessEvents.ToArray() : [],
                    Health = hasData
                        ? new FrameHealth(bucket.Events, bucket.LateEvents, lost, bucket.DroppedFlows, bucket.DecodeFailures)
                        : new FrameHealth(0, 0, lost, 0, 0),
                });
                bucket.Reset(-1);
            }

            if (lastIndex >= _oldestOpen)
            {
                _oldestOpen = lastIndex + 1;
                if (_newestOpen < _oldestOpen - 1)
                {
                    _newestOpen = _oldestOpen - 1;
                }
            }
        }

        return frames;
    }

    private Bucket? BucketFor(long timestamp)
    {
        if (_oldestOpen < 0)
        {
            return null;
        }

        long index = _timeline.IndexOf(timestamp);
        bool late = false;
        if (index < _oldestOpen)
        {
            index = _oldestOpen;
            late = true;
        }
        else if (index - _oldestOpen >= _ring.Length)
        {
            // Event far ahead of the finalization point (finalizer stalled): keep it in the newest slot available.
            index = _oldestOpen + _ring.Length - 1;
            late = true;
        }

        var bucket = _ring[Slot(index)];
        if (bucket.Index != index)
        {
            bucket.Reset(index);
        }

        if (index > _newestOpen)
        {
            _newestOpen = index;
        }

        if (late)
        {
            bucket.LateEvents++;
        }

        return bucket;
    }

    private int Slot(long index) => (int)(index % _ring.Length);

    private sealed class Bucket
    {
        public long Index = -1;
        public readonly Dictionary<ProcessKey, TrafficCounters> Processes = [];
        public readonly Dictionary<FlowKey, FlowCounters> Flows = [];
        public readonly List<ProcessLifecycleEvent> ProcessEvents = [];
        public int Events;
        public int LateEvents;
        public int DroppedFlows;
        public int DecodeFailures;

        public void Reset(long index)
        {
            Index = index;
            Processes.Clear();
            Flows.Clear();
            ProcessEvents.Clear();
            Events = 0;
            LateEvents = 0;
            DroppedFlows = 0;
            DecodeFailures = 0;
        }
    }
}
