using System.Buffers.Binary;
using System.Text;
using WinNetworkManager.Core.Processes;
using WinNetworkManager.Platform.Windows.Etw;
using WinNetworkManager.Platform.Windows.Interop;
using WinNetworkManager.Telemetry.Model;

namespace WinNetworkManager.Telemetry.Decoding;

/// <summary>
/// Decodes Microsoft-Windows-Kernel-Process ProcessStart (1) and ProcessStop (2). Process events are rare, so this
/// decoder locates fields with the variable-length TDH walker and may allocate.
/// </summary>
internal sealed unsafe class KernelProcessDecoder
{
    /// <summary>Provider GUID.</summary>
    public static readonly Guid ProviderId = new("22FB2CD6-0E7B-422B-A0C7-2FAD1FD0E716");

    /// <summary>WINEVENT_KEYWORD_PROCESS.</summary>
    public const ulong KeywordProcess = 0x10;

    private readonly Dictionary<int, TdhSchema?> _schemas = [];

    /// <summary>Events that could not be decoded.</summary>
    public long Failures { get; private set; }

    /// <summary>Decodes a live record; returns null for other events.</summary>
    public ProcessLifecycleEvent? TryDecode(EtwNative.EVENT_RECORD* record)
    {
        ushort id = record->EventHeader.EventDescriptor.Id;
        if (id is not (1 or 2))
        {
            return null;
        }

        int key = (id << 8) | record->EventHeader.EventDescriptor.Version;
        if (!_schemas.TryGetValue(key, out var schema))
        {
            schema = TdhSchema.TryRead(record, out var s, out _) ? s : null;
            _schemas[key] = schema;
        }

        if (schema is null)
        {
            Failures++;
            return null;
        }

        bool is64 = (record->EventHeader.Flags & EtwNative.EVENT_HEADER_FLAG_32_BIT_HEADER) == 0;
        var payload = new ReadOnlySpan<byte>((void*)record->UserData, record->UserDataLength);
        var result = Decode(id, record->EventHeader.TimeStamp, payload, schema, is64);
        if (result is null)
        {
            Failures++;
        }

        return result;
    }

    /// <summary>Decodes a payload with an explicit schema (tests).</summary>
    public static ProcessLifecycleEvent? Decode(ushort id, long timestamp, ReadOnlySpan<byte> payload, TdhSchema schema, bool pointerIs64Bit)
    {
        Span<TdhFieldLocation> locations = schema.Properties.Count <= 64
            ? stackalloc TdhFieldLocation[schema.Properties.Count]
            : new TdhFieldLocation[schema.Properties.Count];
        if (!schema.TryLocate(payload, pointerIs64Bit, locations))
        {
            return null;
        }

        long? pid = ReadUInt(schema, payload, locations, "ProcessID");
        long? createTime = ReadUInt(schema, payload, locations, "CreateTime");
        if (pid is null || createTime is null)
        {
            return null;
        }

        var key = new ProcessKey((int)pid.Value, createTime.Value);
        ulong sequence = (ulong)(ReadUInt(schema, payload, locations, "ProcessSequenceNumber") ?? 0);
        return id == 1
            ? new ProcessLifecycleEvent(
                ProcessLifecycleKind.Started,
                timestamp,
                key,
                (int)(ReadUInt(schema, payload, locations, "ParentProcessID") ?? 0),
                (int)(ReadUInt(schema, payload, locations, "SessionID") ?? -1),
                ReadText(schema, payload, locations, "ImageName"),
                null,
                sequence)
            : new ProcessLifecycleEvent(
                ProcessLifecycleKind.Exited,
                timestamp,
                key,
                0,
                -1,
                ReadText(schema, payload, locations, "ImageName"),
                ReadUInt(schema, payload, locations, "ExitCode") is { } code ? unchecked((int)code) : null,
                sequence);
    }

    private static long? ReadUInt(TdhSchema schema, ReadOnlySpan<byte> payload, ReadOnlySpan<TdhFieldLocation> locations, string name)
    {
        int i = schema.IndexOf(name);
        if (i < 0)
        {
            return null;
        }

        var l = locations[i];
        var slice = payload.Slice(l.Offset, l.Size);
        return l.Size switch
        {
            4 => BinaryPrimitives.ReadUInt32LittleEndian(slice),
            8 => (long)BinaryPrimitives.ReadUInt64LittleEndian(slice),
            2 => BinaryPrimitives.ReadUInt16LittleEndian(slice),
            _ => null,
        };
    }

    private static string? ReadText(TdhSchema schema, ReadOnlySpan<byte> payload, ReadOnlySpan<TdhFieldLocation> locations, string name)
    {
        int i = schema.IndexOf(name);
        if (i < 0)
        {
            return null;
        }

        var l = locations[i];
        var slice = payload.Slice(l.Offset, l.Size);
        if (schema.Properties[i].InType == TdhInType.UnicodeString)
        {
            var chars = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, char>(slice[..(slice.Length & ~1)]);
            int nul = chars.IndexOf('\0');
            return new string(nul >= 0 ? chars[..nul] : chars);
        }

        if (schema.Properties[i].InType == TdhInType.AnsiString)
        {
            int nul = slice.IndexOf((byte)0);
            return Encoding.Latin1.GetString(nul >= 0 ? slice[..nul] : slice);
        }

        return null;
    }
}
