using System.Buffers.Binary;
using System.Runtime.InteropServices;
using WinNetworkManager.Platform.Windows.Interop;
using static WinNetworkManager.Platform.Windows.Interop.EtwNative;

namespace WinNetworkManager.Platform.Windows.Etw;

/// <summary>TDH input types (subset used to size properties).</summary>
internal static class TdhInType
{
    public const ushort UnicodeString = 1;
    public const ushort AnsiString = 2;
    public const ushort Int8 = 3;
    public const ushort UInt8 = 4;
    public const ushort Int16 = 5;
    public const ushort UInt16 = 6;
    public const ushort Int32 = 7;
    public const ushort UInt32 = 8;
    public const ushort Int64 = 9;
    public const ushort UInt64 = 10;
    public const ushort Float = 11;
    public const ushort Double = 12;
    public const ushort Boolean = 13;
    public const ushort Binary = 14;
    public const ushort Guid = 15;
    public const ushort Pointer = 16;
    public const ushort FileTime = 17;
    public const ushort SystemTime = 18;
    public const ushort Sid = 19;
    public const ushort HexInt32 = 20;
    public const ushort HexInt64 = 21;
    public const ushort SizeT = 308;
}

/// <summary>TDH property flags.</summary>
[Flags]
internal enum TdhPropertyFlags : uint
{
    None = 0,
    Struct = 0x1,
    ParamLength = 0x2,
    ParamCount = 0x4,
    WbemXmlFragment = 0x8,
    ParamFixedLength = 0x10,
    ParamFixedCount = 0x20,
    HasTags = 0x40,
    HasCustomSchema = 0x80,
}

/// <summary>One top-level property of an event template.</summary>
/// <param name="Name">Property name.</param>
/// <param name="InType">TDH input type.</param>
/// <param name="OutType">TDH output type (formatting hint, e.g. IPv4, port).</param>
/// <param name="Flags">Property flags.</param>
/// <param name="LengthOrIndex">Fixed length, or index of the length property when <see cref="TdhPropertyFlags.ParamLength"/>.</param>
/// <param name="CountOrIndex">Fixed count, or index of the count property when <see cref="TdhPropertyFlags.ParamCount"/>.</param>
internal readonly record struct TdhProperty(
    string Name,
    ushort InType,
    ushort OutType,
    TdhPropertyFlags Flags,
    ushort LengthOrIndex,
    ushort CountOrIndex);

/// <summary>Location of a property inside one specific payload.</summary>
internal readonly record struct TdhFieldLocation(int Offset, int Size);

/// <summary>
/// Event template obtained from <c>TdhGetEventInformation</c>. Resolved once per (provider, id, version) and
/// cached by callers; used to locate fields by name so decoders do not depend on hard-coded layouts.
/// </summary>
internal sealed unsafe class TdhSchema
{
    private TdhSchema(string taskName, string opcodeName, TdhProperty[] properties)
    {
        TaskName = taskName;
        OpcodeName = opcodeName;
        Properties = properties;
    }

    /// <summary>Task name from the manifest.</summary>
    public string TaskName { get; }

    /// <summary>Opcode name from the manifest.</summary>
    public string OpcodeName { get; }

    /// <summary>Top-level properties in payload order.</summary>
    public IReadOnlyList<TdhProperty> Properties { get; }

    /// <summary>Reads the schema of the event in <paramref name="record"/>.</summary>
    public static bool TryRead(EVENT_RECORD* record, out TdhSchema? schema, out uint error)
    {
        schema = null;
        uint size = 0;
        error = TdhGetEventInformation(record, 0, 0, null, &size);
        if (error != Win32Error.ERROR_INSUFFICIENT_BUFFER)
        {
            return false;
        }

        byte* buffer = (byte*)NativeMemory.Alloc(size);
        try
        {
            error = TdhGetEventInformation(record, 0, 0, buffer, &size);
            if (error != Win32Error.ERROR_SUCCESS)
            {
                return false;
            }

            uint topCount = *(uint*)(buffer + TRACE_EVENT_INFO_TopLevelPropertyCountOffset);
            var properties = new TdhProperty[topCount];
            for (int i = 0; i < topCount; i++)
            {
                byte* p = buffer + TRACE_EVENT_INFO_PropertyArrayOffset + (i * EVENT_PROPERTY_INFO_Size);
                properties[i] = new TdhProperty(
                    ReadString(buffer, *(uint*)(p + 4)),
                    *(ushort*)(p + 8),
                    *(ushort*)(p + 10),
                    (TdhPropertyFlags)(*(uint*)p),
                    *(ushort*)(p + 18),
                    *(ushort*)(p + 16));
            }

            schema = new TdhSchema(
                ReadString(buffer, *(uint*)(buffer + TRACE_EVENT_INFO_TaskNameOffsetOffset)),
                ReadString(buffer, *(uint*)(buffer + TRACE_EVENT_INFO_OpcodeNameOffsetOffset)),
                properties);
            error = Win32Error.ERROR_SUCCESS;
            return true;
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }

    /// <summary>Creates a schema from explicit properties (tests, fallbacks).</summary>
    public static TdhSchema FromProperties(string taskName, string opcodeName, params TdhProperty[] properties) =>
        new(taskName, opcodeName, properties);

    /// <summary>Index of a property by name, or -1.</summary>
    public int IndexOf(string name)
    {
        for (int i = 0; i < Properties.Count; i++)
        {
            if (string.Equals(Properties[i].Name, name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Offsets of properties that precede the first variable-length property (identical for every event with this
    /// schema). Properties after it get -1.
    /// </summary>
    public int[] ComputeFixedOffsets(bool pointerIs64Bit)
    {
        var offsets = new int[Properties.Count];
        int offset = 0;
        bool fixedSoFar = true;
        for (int i = 0; i < Properties.Count; i++)
        {
            offsets[i] = fixedSoFar ? offset : -1;
            int size = fixedSoFar ? FixedSizeOf(Properties[i], pointerIs64Bit) : -1;
            if (size < 0)
            {
                fixedSoFar = false;
            }
            else
            {
                offset += size;
            }
        }

        return offsets;
    }

    /// <summary>Size of a property whose size does not depend on the payload, or -1.</summary>
    public static int FixedSizeOf(TdhProperty property, bool pointerIs64Bit)
    {
        if ((property.Flags & (TdhPropertyFlags.Struct | TdhPropertyFlags.ParamCount | TdhPropertyFlags.ParamLength)) != 0)
        {
            return -1;
        }

        int count = property.CountOrIndex > 1 ? property.CountOrIndex : 1;
        int size = ElementFixedSize(property, pointerIs64Bit);
        return size < 0 ? -1 : size * count;
    }

    /// <summary>
    /// Locates every top-level property inside <paramref name="payload"/>, handling variable-length strings, SIDs,
    /// parameterized lengths/counts and pointer size. Returns false on a layout it cannot size or on truncation.
    /// </summary>
    public bool TryLocate(ReadOnlySpan<byte> payload, bool pointerIs64Bit, Span<TdhFieldLocation> locations)
    {
        if (locations.Length < Properties.Count)
        {
            throw new ArgumentException("Location span too small.", nameof(locations));
        }

        int offset = 0;
        for (int i = 0; i < Properties.Count; i++)
        {
            var property = Properties[i];
            if ((property.Flags & TdhPropertyFlags.Struct) != 0)
            {
                return false;
            }

            long count = 1;
            if ((property.Flags & TdhPropertyFlags.ParamCount) != 0)
            {
                if (!TryReadInteger(payload, locations, property.CountOrIndex, i, out count) || count is < 0 or > 65_535)
                {
                    return false;
                }
            }
            else if (property.CountOrIndex > 1)
            {
                count = property.CountOrIndex;
            }

            int total = 0;
            for (long n = 0; n < count; n++)
            {
                int size = ElementSize(property, payload, offset + total, pointerIs64Bit, locations, i);
                if (size < 0)
                {
                    return false;
                }

                total += size;
            }

            if (offset + total > payload.Length)
            {
                return false;
            }

            locations[i] = new TdhFieldLocation(offset, total);
            offset += total;
        }

        return true;
    }

    private static int ElementFixedSize(TdhProperty property, bool pointerIs64Bit) => property.InType switch
    {
        TdhInType.Int8 or TdhInType.UInt8 => 1,
        TdhInType.Int16 or TdhInType.UInt16 => 2,
        TdhInType.Int32 or TdhInType.UInt32 or TdhInType.Float or TdhInType.Boolean or TdhInType.HexInt32 => 4,
        TdhInType.Int64 or TdhInType.UInt64 or TdhInType.Double or TdhInType.FileTime or TdhInType.HexInt64 => 8,
        TdhInType.Guid or TdhInType.SystemTime => 16,
        TdhInType.Pointer or TdhInType.SizeT => pointerIs64Bit ? 8 : 4,
        TdhInType.Binary when property.LengthOrIndex > 0 && (property.Flags & TdhPropertyFlags.ParamLength) == 0 => property.LengthOrIndex,
        TdhInType.UnicodeString when property.LengthOrIndex > 0 && (property.Flags & TdhPropertyFlags.ParamLength) == 0 => property.LengthOrIndex * 2,
        TdhInType.AnsiString when property.LengthOrIndex > 0 && (property.Flags & TdhPropertyFlags.ParamLength) == 0 => property.LengthOrIndex,
        _ => -1,
    };

    private int ElementSize(TdhProperty property, ReadOnlySpan<byte> payload, int offset, bool pointerIs64Bit, ReadOnlySpan<TdhFieldLocation> resolved, int currentIndex)
    {
        if ((property.Flags & TdhPropertyFlags.ParamLength) != 0)
        {
            if (!TryReadInteger(payload, resolved, property.LengthOrIndex, currentIndex, out long length) || length is < 0 or > 1_048_576)
            {
                return -1;
            }

            return property.InType == TdhInType.UnicodeString ? (int)length * 2 : (int)length;
        }

        int fixedSize = ElementFixedSize(property, pointerIs64Bit);
        if (fixedSize >= 0)
        {
            return fixedSize;
        }

        ReadOnlySpan<byte> remaining = offset < payload.Length ? payload[offset..] : [];
        switch (property.InType)
        {
            case TdhInType.UnicodeString:
                for (int i = 0; i + 1 < remaining.Length; i += 2)
                {
                    if (remaining[i] == 0 && remaining[i + 1] == 0)
                    {
                        return i + 2;
                    }
                }

                return remaining.Length; // unterminated string at the end of the payload

            case TdhInType.AnsiString:
            {
                int nul = remaining.IndexOf((byte)0);
                return nul < 0 ? remaining.Length : nul + 1;
            }

            case TdhInType.Sid:
                // SID: Revision(1) SubAuthorityCount(1) IdentifierAuthority(6) SubAuthority[n](4n).
                if (remaining.Length >= 8 && remaining[0] == 1)
                {
                    return 8 + (4 * remaining[1]);
                }

                return pointerIs64Bit ? 8 : 4; // NULL SID encoded as a pointer-sized zero

            default:
                return -1;
        }
    }

    private static bool TryReadInteger(ReadOnlySpan<byte> payload, ReadOnlySpan<TdhFieldLocation> resolved, int index, int currentIndex, out long value)
    {
        value = -1;
        if (index < 0 || index >= currentIndex)
        {
            return false; // the referenced property must precede the current one
        }

        var location = resolved[index];
        if (location.Size == 0 || location.Offset + location.Size > payload.Length)
        {
            return false;
        }

        var slice = payload.Slice(location.Offset, location.Size);
        value = location.Size switch
        {
            1 => slice[0],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(slice),
            4 => BinaryPrimitives.ReadUInt32LittleEndian(slice),
            8 => (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(slice), long.MaxValue),
            _ => -1,
        };
        return value >= 0;
    }

    private static string ReadString(byte* buffer, uint offset) => offset == 0 ? string.Empty : new string((char*)(buffer + offset));
}
