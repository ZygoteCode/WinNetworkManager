using System.Runtime.InteropServices;

namespace WinNetworkManager.Platform.Windows.Interop;

#pragma warning disable SA1307, SA1310, IDE1006 // Native names are kept verbatim.

/// <summary>Raw ETW controller/consumer API (evntrace.h, evntcons.h, tdh.h). Layouts verified by tests (x64/ARM64).</summary>
internal static unsafe partial class EtwNative
{
    public const uint WNODE_FLAG_TRACED_GUID = 0x00020000;
    public const uint EVENT_TRACE_REAL_TIME_MODE = 0x00000100;
    public const uint EVENT_TRACE_USE_MS_FLUSH_TIMER = 0x00000010;
    public const uint PROCESS_TRACE_MODE_REAL_TIME = 0x00000100;
    public const uint PROCESS_TRACE_MODE_RAW_TIMESTAMP = 0x00001000;
    public const uint PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;

    public const uint EVENT_TRACE_CONTROL_QUERY = 0;
    public const uint EVENT_TRACE_CONTROL_STOP = 1;
    public const uint EVENT_TRACE_CONTROL_UPDATE = 2;
    public const uint EVENT_TRACE_CONTROL_FLUSH = 3;

    public const uint EVENT_CONTROL_CODE_DISABLE_PROVIDER = 0;
    public const uint EVENT_CONTROL_CODE_ENABLE_PROVIDER = 1;

    public const ushort EVENT_HEADER_FLAG_32_BIT_HEADER = 0x0020;
    public const ushort EVENT_HEADER_FLAG_64_BIT_HEADER = 0x0040;

    public const ulong INVALID_PROCESSTRACE_HANDLE = ulong.MaxValue;

    /// <summary>QPC clock for timestamps (WNODE_HEADER.ClientContext).</summary>
    public const uint CLOCK_QPC = 1;

    /// <summary>Maximum session name length accepted by ETW (characters, including terminator).</summary>
    public const int MaxLoggerNameChars = 1024;

    [StructLayout(LayoutKind.Sequential)]
    public struct WNODE_HEADER
    {
        public uint BufferSize;
        public uint ProviderId;
        public ulong HistoricalContext;
        public long TimeStamp;
        public Guid Guid;
        public uint ClientContext;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EVENT_TRACE_PROPERTIES
    {
        public WNODE_HEADER Wnode;
        public uint BufferSize;
        public uint MinimumBuffers;
        public uint MaximumBuffers;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint FlushTimer;
        public uint EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers;
        public uint FreeBuffers;
        public uint EventsLost;
        public uint BuffersWritten;
        public uint LogBuffersLost;
        public uint RealTimeBuffersLost;
        public nint LoggerThreadId;
        public uint LogFileNameOffset;
        public uint LoggerNameOffset;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EVENT_DESCRIPTOR
    {
        public ushort Id;
        public byte Version;
        public byte Channel;
        public byte Level;
        public byte Opcode;
        public ushort Task;
        public ulong Keyword;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EVENT_HEADER
    {
        public ushort Size;
        public ushort HeaderType;
        public ushort Flags;
        public ushort EventProperty;
        public uint ThreadId;
        public uint ProcessId;
        public long TimeStamp;
        public Guid ProviderId;
        public EVENT_DESCRIPTOR EventDescriptor;
        public ulong ProcessorTime;
        public Guid ActivityId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EVENT_RECORD
    {
        public EVENT_HEADER EventHeader;
        public ushort ProcessorIndex;
        public ushort LoggerId;
        public ushort ExtendedDataCount;
        public ushort UserDataLength;
        public nint ExtendedData;
        public nint UserData;
        public nint UserContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EVENT_TRACE_HEADER
    {
        public ushort Size;
        public ushort FieldTypeFlags;
        public uint Version;
        public uint ThreadId;
        public uint ProcessId;
        public long TimeStamp;
        public Guid Guid;
        public ulong ProcessorTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EVENT_TRACE
    {
        public EVENT_TRACE_HEADER Header;
        public uint InstanceId;
        public uint ParentInstanceId;
        public Guid ParentGuid;
        public nint MofData;
        public uint MofLength;
        public uint ClientContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEMTIME
    {
        public ushort Year;
        public ushort Month;
        public ushort DayOfWeek;
        public ushort Day;
        public ushort Hour;
        public ushort Minute;
        public ushort Second;
        public ushort Milliseconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TIME_ZONE_INFORMATION
    {
        public int Bias;
        public fixed char StandardName[32];
        public SYSTEMTIME StandardDate;
        public int StandardBias;
        public fixed char DaylightName[32];
        public SYSTEMTIME DaylightDate;
        public int DaylightBias;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TRACE_LOGFILE_HEADER
    {
        public uint BufferSize;
        public uint Version;
        public uint ProviderVersion;
        public uint NumberOfProcessors;
        public long EndTime;
        public uint TimerResolution;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint BuffersWritten;
        public uint StartBuffers;
        public uint PointerSize;
        public uint EventsLost;
        public uint CpuSpeedInMHz;
        public nint LoggerName;
        public nint LogFileName;
        public TIME_ZONE_INFORMATION TimeZone;
        public long BootTime;
        public long PerfFreq;
        public long StartTime;
        public uint ReservedFlags;
        public uint BuffersLost;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EVENT_TRACE_LOGFILEW
    {
        public nint LogFileName;
        public nint LoggerName;
        public long CurrentTime;
        public uint BuffersRead;
        public uint ProcessTraceMode;
        public EVENT_TRACE CurrentEvent;
        public TRACE_LOGFILE_HEADER LogfileHeader;
        public nint BufferCallback;
        public uint BufferSize;
        public uint Filled;
        public uint EventsLost;
        public nint EventRecordCallback;
        public uint IsKernelTrace;
        public nint Context;
    }

    // TRACE_EVENT_INFO / EVENT_PROPERTY_INFO are parsed by offset (variable length), see TdhSchema.
    public const int TRACE_EVENT_INFO_PropertyCountOffset = 100;
    public const int TRACE_EVENT_INFO_TopLevelPropertyCountOffset = 104;
    public const int TRACE_EVENT_INFO_TaskNameOffsetOffset = 68;
    public const int TRACE_EVENT_INFO_OpcodeNameOffsetOffset = 72;
    public const int TRACE_EVENT_INFO_PropertyArrayOffset = 112;
    public const int EVENT_PROPERTY_INFO_Size = 24;

    [LibraryImport("advapi32.dll", EntryPoint = "StartTraceW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint StartTrace(out ulong sessionHandle, string sessionName, EVENT_TRACE_PROPERTIES* properties);

    [LibraryImport("advapi32.dll", EntryPoint = "ControlTraceW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint ControlTrace(ulong sessionHandle, string? sessionName, EVENT_TRACE_PROPERTIES* properties, uint controlCode);

    [LibraryImport("advapi32.dll")]
    public static partial uint EnableTraceEx2(
        ulong traceHandle,
        Guid* providerId,
        uint controlCode,
        byte level,
        ulong matchAnyKeyword,
        ulong matchAllKeyword,
        uint timeout,
        nint enableParameters);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenTraceW")]
    public static partial ulong OpenTrace(EVENT_TRACE_LOGFILEW* logfile);

    [LibraryImport("advapi32.dll")]
    public static partial uint ProcessTrace(ulong* handleArray, uint handleCount, nint startTime, nint endTime);

    [LibraryImport("advapi32.dll")]
    public static partial uint CloseTrace(ulong traceHandle);

    [LibraryImport("tdh.dll")]
    public static partial uint TdhGetEventInformation(EVENT_RECORD* eventRecord, uint tdhContextCount, nint tdhContext, byte* buffer, uint* bufferSize);
}
