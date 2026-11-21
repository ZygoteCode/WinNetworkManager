using WinNetworkManager.Core.Processes;
using WinNetworkManager.Core.Traffic;

namespace WinNetworkManager.Telemetry.Model;

/// <summary>Traffic of one process instance in one bucket.</summary>
/// <param name="Process">Process instance.</param>
/// <param name="Counters">Counters.</param>
public readonly record struct ProcessTraffic(ProcessKey Process, TrafficCounters Counters);

/// <summary>Traffic of one flow in one bucket.</summary>
/// <param name="Key">Flow identity.</param>
/// <param name="Counters">Counters.</param>
public readonly record struct FlowTraffic(FlowKey Key, FlowCounters Counters);

/// <summary>Kind of a process lifecycle event.</summary>
public enum ProcessLifecycleKind : byte
{
    /// <summary>Process started.</summary>
    Started = 1,

    /// <summary>Process exited.</summary>
    Exited = 2,
}

/// <summary>Process start/stop observed by the collector (Kernel-Process provider).</summary>
/// <param name="Kind">Start or exit.</param>
/// <param name="Timestamp">QPC timestamp.</param>
/// <param name="Process">Instance key (PID + creation time).</param>
/// <param name="ParentPid">Parent PID (start events).</param>
/// <param name="SessionId">Session id (start events), -1 when unknown.</param>
/// <param name="ImageName">NT path (start) or file name (exit) of the image, when present.</param>
/// <param name="ExitCode">Exit code (exit events).</param>
/// <param name="SequenceNumber">Boot-unique process sequence number, 0 when unknown.</param>
public sealed record ProcessLifecycleEvent(
    ProcessLifecycleKind Kind,
    long Timestamp,
    ProcessKey Process,
    int ParentPid,
    int SessionId,
    string? ImageName,
    int? ExitCode,
    ulong SequenceNumber);

/// <summary>Quality information attached to each frame.</summary>
/// <param name="Events">Network events aggregated in the bucket.</param>
/// <param name="LateEvents">Events that arrived after their own bucket was finalized and were added to this one.</param>
/// <param name="EventsLost">Events ETW reported as lost since the previous frame (data in this frame is partial).</param>
/// <param name="DroppedFlows">Flows not tracked individually because the per-bucket flow cap was reached.</param>
/// <param name="DecodeFailures">Events that could not be decoded.</param>
public readonly record struct FrameHealth(int Events, int LateEvents, long EventsLost, int DroppedFlows, int DecodeFailures)
{
    /// <summary>True when the frame may be missing data.</summary>
    public bool IsPartial => EventsLost > 0 || DecodeFailures > 0;
}

/// <summary>
/// All traffic observed in one finalized bucket of the QPC timeline. Frames are emitted for every bucket in order,
/// including empty ones, so consumers know exactly which time ranges are complete.
/// </summary>
public sealed class TelemetryFrame
{
    /// <summary>Bucket index (timestamp / bucket ticks).</summary>
    public required long BucketIndex { get; init; }

    /// <summary>First QPC timestamp covered.</summary>
    public required long StartTimestamp { get; init; }

    /// <summary>First QPC timestamp after the bucket.</summary>
    public required long EndTimestamp { get; init; }

    /// <summary>Per-process traffic (only processes with traffic).</summary>
    public required IReadOnlyList<ProcessTraffic> Processes { get; init; }

    /// <summary>Per-flow traffic.</summary>
    public required IReadOnlyList<FlowTraffic> Flows { get; init; }

    /// <summary>Process starts/exits in the bucket.</summary>
    public required IReadOnlyList<ProcessLifecycleEvent> ProcessEvents { get; init; }

    /// <summary>Quality information.</summary>
    public required FrameHealth Health { get; init; }

    /// <summary>True when the bucket contains nothing.</summary>
    public bool IsEmpty => Processes.Count == 0 && Flows.Count == 0 && ProcessEvents.Count == 0;
}

/// <summary>Lifecycle state of a collector.</summary>
public enum CollectorState : byte
{
    /// <summary>Not running.</summary>
    Stopped,

    /// <summary>Starting the session.</summary>
    Starting,

    /// <summary>Receiving events.</summary>
    Running,

    /// <summary>Failed; will retry automatically.</summary>
    Retrying,

    /// <summary>Failed permanently (e.g. access denied).</summary>
    Faulted,
}

/// <summary>Collector health snapshot, published about once per second.</summary>
/// <param name="State">State.</param>
/// <param name="SessionName">ETW session name.</param>
/// <param name="EventsPerSecond">Recent event rate.</param>
/// <param name="TotalEvents">Events since start.</param>
/// <param name="TotalEventsLost">Events lost since start (ETW buffers).</param>
/// <param name="TotalLateEvents">Late events since start.</param>
/// <param name="TotalDecodeFailures">Undecodable events since start.</param>
/// <param name="Restarts">Automatic restarts.</param>
/// <param name="UsesMillisecondFlushTimer">Low-latency flush timer available.</param>
/// <param name="LastError">Last error message, if any.</param>
/// <param name="IsAccessDenied">True when the last failure was a privilege problem.</param>
public sealed record CollectorStatus(
    CollectorState State,
    string SessionName,
    double EventsPerSecond,
    long TotalEvents,
    long TotalEventsLost,
    long TotalLateEvents,
    long TotalDecodeFailures,
    int Restarts,
    bool UsesMillisecondFlushTimer,
    string? LastError,
    bool IsAccessDenied)
{
    /// <summary>Status of a collector that is not running.</summary>
    public static CollectorStatus NotRunning(string sessionName, string? error = null, bool accessDenied = false) =>
        new(error is null ? CollectorState.Stopped : CollectorState.Faulted, sessionName, 0, 0, 0, 0, 0, 0, false, error, accessDenied);
}

/// <summary>Receives collector output. Called from collector threads; implementations must not block.</summary>
public interface ITelemetrySink
{
    /// <summary>A finalized bucket.</summary>
    void OnFrame(TelemetryFrame frame);

    /// <summary>Periodic status.</summary>
    void OnStatus(CollectorStatus status);
}
