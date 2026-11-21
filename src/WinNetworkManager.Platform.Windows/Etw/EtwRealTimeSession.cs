using System.Runtime.InteropServices;
using WinNetworkManager.Platform.Windows.Interop;
using static WinNetworkManager.Platform.Windows.Interop.EtwNative;

namespace WinNetworkManager.Platform.Windows.Etw;

/// <summary>Parameters of a real-time ETW session.</summary>
/// <param name="Name">Session name. A fixed name lets a new instance stop a session left behind by a crash.</param>
/// <param name="BufferSizeKb">Size of each buffer in KB.</param>
/// <param name="MinimumBuffers">Minimum number of buffers.</param>
/// <param name="MaximumBuffers">Maximum number of buffers.</param>
/// <param name="FlushInterval">How often partially filled buffers are delivered (lower = less latency).</param>
public sealed record EtwSessionOptions(
    string Name,
    uint BufferSizeKb = 64,
    uint MinimumBuffers = 16,
    uint MaximumBuffers = 256,
    TimeSpan? FlushInterval = null)
{
    /// <summary>Effective flush interval (default 200 ms).</summary>
    public TimeSpan EffectiveFlushInterval => FlushInterval ?? TimeSpan.FromMilliseconds(200);
}

/// <summary>Counters reported by the session (cumulative since start).</summary>
/// <param name="EventsLost">Events dropped by ETW (buffers full).</param>
/// <param name="RealTimeBuffersLost">Buffers that could not be delivered to the real-time consumer.</param>
/// <param name="LogBuffersLost">Buffers lost before being written.</param>
/// <param name="BuffersWritten">Buffers flushed.</param>
/// <param name="NumberOfBuffers">Buffers currently allocated.</param>
/// <param name="FreeBuffers">Buffers currently free.</param>
public readonly record struct EtwSessionStatistics(
    uint EventsLost,
    uint RealTimeBuffersLost,
    uint LogBuffersLost,
    uint BuffersWritten,
    uint NumberOfBuffers,
    uint FreeBuffers);

/// <summary>
/// Controller side of a real-time ETW session (StartTrace / EnableTraceEx2 / ControlTrace). Sessions are kernel
/// objects that outlive the process, so <see cref="Stop"/> must run on every exit path and <see cref="Start"/> first
/// stops a stale session with the same name.
/// </summary>
public sealed unsafe class EtwRealTimeSession : IDisposable
{
    private static readonly int PropertiesSize = sizeof(EVENT_TRACE_PROPERTIES) + (MaxLoggerNameChars * sizeof(char) * 2);
    private readonly object _gate = new();
    private ulong _handle;
    private bool _stopped;

    private EtwRealTimeSession(string name, ulong handle, bool millisecondFlush)
    {
        Name = name;
        _handle = handle;
        UsesMillisecondFlushTimer = millisecondFlush;
    }

    /// <summary>Session name.</summary>
    public string Name { get; }

    /// <summary>True if the session uses the millisecond flush timer (Windows 8+); false when it fell back to 1 s.</summary>
    public bool UsesMillisecondFlushTimer { get; }

    /// <summary>True until <see cref="Stop"/> runs.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return !_stopped;
            }
        }
    }

    /// <summary>Starts a session, stopping a stale one with the same name.</summary>
    /// <exception cref="NativeCallException">The session could not be created (e.g. access denied).</exception>
    public static EtwRealTimeSession Start(EtwSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Name.Length is 0 or >= MaxLoggerNameChars)
        {
            throw new ArgumentException("Invalid session name length.", nameof(options));
        }

        TryStopExisting(options.Name);

        uint flushMs = (uint)Math.Clamp(options.EffectiveFlushInterval.TotalMilliseconds, 1, 60_000);
        uint error = TryStart(options, millisecondFlush: true, flushMs, out ulong handle);
        if (error == Win32Error.ERROR_ALREADY_EXISTS)
        {
            // Another instance raced us or the stop did not complete; stop again once.
            TryStopExisting(options.Name);
            error = TryStart(options, millisecondFlush: true, flushMs, out handle);
        }

        if (error == Win32Error.ERROR_INVALID_PARAMETER)
        {
            // Older systems without EVENT_TRACE_USE_MS_FLUSH_TIMER: fall back to the 1 s timer.
            uint seconds = Math.Max(1, (uint)Math.Ceiling(flushMs / 1000.0));
            error = TryStart(options, millisecondFlush: false, seconds, out handle);
            if (error == Win32Error.ERROR_SUCCESS)
            {
                return new EtwRealTimeSession(options.Name, handle, millisecondFlush: false);
            }
        }

        if (error != Win32Error.ERROR_SUCCESS)
        {
            throw new NativeCallException("StartTrace", error);
        }

        return new EtwRealTimeSession(options.Name, handle, millisecondFlush: true);
    }

    /// <summary>Stops a session by name. Returns true when a session existed and was stopped.</summary>
    public static bool TryStopExisting(string name)
    {
        byte* buffer = stackalloc byte[PropertiesSize];
        var properties = InitProperties(buffer);
        uint error = ControlTrace(0, name, properties, EVENT_TRACE_CONTROL_STOP);
        return error == Win32Error.ERROR_SUCCESS;
    }

    /// <summary>Enables a provider in this session.</summary>
    public void EnableProvider(Guid provider, byte level, ulong matchAnyKeyword, ulong matchAllKeyword = 0)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            uint error = EnableTraceEx2(_handle, &provider, EVENT_CONTROL_CODE_ENABLE_PROVIDER, level, matchAnyKeyword, matchAllKeyword, 0, 0);
            if (error != Win32Error.ERROR_SUCCESS)
            {
                throw new NativeCallException("EnableTraceEx2", error);
            }
        }
    }

    /// <summary>Queries the session counters.</summary>
    public EtwSessionStatistics QueryStatistics()
    {
        byte* buffer = stackalloc byte[PropertiesSize];
        var properties = InitProperties(buffer);
        uint error;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            error = ControlTrace(_handle, null, properties, EVENT_TRACE_CONTROL_QUERY);
        }

        if (error != Win32Error.ERROR_SUCCESS)
        {
            throw new NativeCallException("ControlTrace(QUERY)", error);
        }

        return new EtwSessionStatistics(
            properties->EventsLost,
            properties->RealTimeBuffersLost,
            properties->LogBuffersLost,
            properties->BuffersWritten,
            properties->NumberOfBuffers,
            properties->FreeBuffers);
    }

    /// <summary>Delivers partially filled buffers to the consumer now.</summary>
    public void Flush()
    {
        byte* buffer = stackalloc byte[PropertiesSize];
        var properties = InitProperties(buffer);
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            _ = ControlTrace(_handle, null, properties, EVENT_TRACE_CONTROL_FLUSH);
        }
    }

    /// <summary>Stops the session (idempotent). The consumer's ProcessTrace returns after draining.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            byte* buffer = stackalloc byte[PropertiesSize];
            var properties = InitProperties(buffer);
            uint error = ControlTrace(_handle, null, properties, EVENT_TRACE_CONTROL_STOP);
            if (error is not Win32Error.ERROR_SUCCESS and not Win32Error.ERROR_WMI_INSTANCE_NOT_FOUND)
            {
                // Fall back to stopping by name (handle may have become invalid).
                _ = TryStopExisting(Name);
            }

            _handle = 0;
        }
    }

    /// <inheritdoc />
    public void Dispose() => Stop();

    private static uint TryStart(EtwSessionOptions options, bool millisecondFlush, uint flushValue, out ulong handle)
    {
        byte* buffer = stackalloc byte[PropertiesSize];
        var properties = InitProperties(buffer);
        properties->Wnode.Flags = WNODE_FLAG_TRACED_GUID;
        properties->Wnode.ClientContext = CLOCK_QPC;
        properties->Wnode.Guid = Guid.NewGuid();
        properties->BufferSize = options.BufferSizeKb;
        properties->MinimumBuffers = options.MinimumBuffers;
        properties->MaximumBuffers = Math.Max(options.MaximumBuffers, options.MinimumBuffers);
        properties->LogFileMode = EVENT_TRACE_REAL_TIME_MODE | (millisecondFlush ? EVENT_TRACE_USE_MS_FLUSH_TIMER : 0);
        properties->FlushTimer = flushValue;
        return StartTrace(out handle, options.Name, properties);
    }

    private static EVENT_TRACE_PROPERTIES* InitProperties(byte* buffer)
    {
        new Span<byte>(buffer, PropertiesSize).Clear();
        var properties = (EVENT_TRACE_PROPERTIES*)buffer;
        properties->Wnode.BufferSize = (uint)PropertiesSize;
        properties->LoggerNameOffset = (uint)sizeof(EVENT_TRACE_PROPERTIES);
        properties->LogFileNameOffset = (uint)(sizeof(EVENT_TRACE_PROPERTIES) + (MaxLoggerNameChars * sizeof(char)));
        return properties;
    }

    /// <summary>Returns true when <paramref name="error"/> means the caller lacks the right to control sessions.</summary>
    public static bool IsAccessDenied(Exception exception) =>
        exception is NativeCallException { Error: Win32Error.ERROR_ACCESS_DENIED } ||
        exception is UnauthorizedAccessException ||
        (exception is ExternalException ee && ee.ErrorCode == (int)Win32Error.ERROR_ACCESS_DENIED);
}
