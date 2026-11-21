using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using WinNetworkManager.Platform.Windows.Interop;
using static WinNetworkManager.Platform.Windows.Interop.EtwNative;

namespace WinNetworkManager.Platform.Windows.Etw;

/// <summary>Receives raw event records on the consumer thread. Implementations must be fast and must not throw.</summary>
internal unsafe interface IEtwEventSink
{
    /// <summary>Called once per event, sequentially, on the consumer thread.</summary>
    void OnEvent(EVENT_RECORD* record);
}

/// <summary>
/// Real-time ETW consumer: OpenTrace + a dedicated thread blocked in ProcessTrace. Timestamps are delivered as raw
/// QPC values (comparable with <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/>).
/// </summary>
internal sealed unsafe class EtwRealTimeConsumer : IDisposable
{
    private readonly IEtwEventSink _sink;
    private readonly string _sessionName;
    private readonly TaskCompletionSource<uint> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private GCHandle _self;
    private nint _loggerName;
    private ulong _traceHandle = INVALID_PROCESSTRACE_HANDLE;
    private Thread? _thread;
    private int _disposed;
    private long _callbackFailures;

    public EtwRealTimeConsumer(string sessionName, IEtwEventSink sink)
    {
        _sessionName = sessionName;
        _sink = sink;
    }

    /// <summary>Completes with the ProcessTrace return code when the consumer stops.</summary>
    public Task<uint> Completion => _completion.Task;

    /// <summary>Exceptions caught inside the native callback (should stay 0).</summary>
    public long CallbackFailures => Interlocked.Read(ref _callbackFailures);

    /// <summary>QPC frequency reported by the trace header.</summary>
    public long PerfFrequency { get; private set; }

    /// <summary>Opens the trace and starts the consumer thread.</summary>
    public void Start()
    {
        if (_thread is not null)
        {
            throw new InvalidOperationException("Consumer already started.");
        }

        _self = GCHandle.Alloc(this);
        _loggerName = Marshal.StringToHGlobalUni(_sessionName);
        var logfile = default(EVENT_TRACE_LOGFILEW);
        logfile.LoggerName = _loggerName;
        logfile.ProcessTraceMode = PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD | PROCESS_TRACE_MODE_RAW_TIMESTAMP;
        logfile.EventRecordCallback = (nint)(delegate* unmanaged<EVENT_RECORD*, void>)&OnEventRecord;
        logfile.Context = GCHandle.ToIntPtr(_self);

        ulong handle = OpenTrace(&logfile);
        if (handle == INVALID_PROCESSTRACE_HANDLE)
        {
            var error = (uint)Marshal.GetLastSystemError();
            Cleanup();
            throw new NativeCallException("OpenTrace", error == 0 ? Win32Error.ERROR_INVALID_HANDLE : error);
        }

        _traceHandle = handle;
        PerfFrequency = logfile.LogfileHeader.PerfFreq;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "ETW consumer: " + _sessionName,
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    private void Run()
    {
        uint result;
        try
        {
            ulong handle = _traceHandle;
            result = ProcessTrace(&handle, 1, 0, 0);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _completion.TrySetException(ex);
            return;
        }

        _completion.TrySetResult(result);
    }

    [UnmanagedCallersOnly]
    private static void OnEventRecord(EVENT_RECORD* record)
    {
        EtwRealTimeConsumer? consumer = null;
        try
        {
            consumer = GCHandle.FromIntPtr(record->UserContext).Target as EtwRealTimeConsumer;
            consumer?._sink.OnEvent(record);
        }
        catch (Exception)
        {
            // Exceptions must never cross the native boundary; count and continue.
            if (consumer is not null)
            {
                Interlocked.Increment(ref consumer._callbackFailures);
            }
        }
    }

    /// <summary>Closes the trace (ProcessTrace returns) and waits briefly for the thread.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_traceHandle != INVALID_PROCESSTRACE_HANDLE)
        {
            _ = CloseTrace(_traceHandle);
        }

        if (_thread is not null && _thread.ManagedThreadId != Environment.CurrentManagedThreadId)
        {
            _thread.Join(TimeSpan.FromSeconds(5));
        }

        Cleanup();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Cleanup()
    {
        _traceHandle = INVALID_PROCESSTRACE_HANDLE;
        if (_loggerName != 0)
        {
            Marshal.FreeHGlobal(_loggerName);
            _loggerName = 0;
        }

        // Only free the handle once the consumer thread can no longer call back.
        if (_self.IsAllocated && (_thread is null || !_thread.IsAlive))
        {
            _self.Free();
        }
    }
}
