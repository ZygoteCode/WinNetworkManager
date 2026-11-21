using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Text;

namespace WinNetworkManager.Diagnostics;

/// <summary>DNS record types supported by the query tool (others are shown in RFC 3597 generic form).</summary>
public enum DnsRecordType : ushort
{
    /// <summary>IPv4 address.</summary>
    A = 1,

    /// <summary>Name server.</summary>
    NS = 2,

    /// <summary>Canonical name (alias).</summary>
    CNAME = 5,

    /// <summary>Start of authority.</summary>
    SOA = 6,

    /// <summary>Pointer (reverse lookup).</summary>
    PTR = 12,

    /// <summary>Mail exchanger.</summary>
    MX = 15,

    /// <summary>Text.</summary>
    TXT = 16,

    /// <summary>IPv6 address.</summary>
    AAAA = 28,

    /// <summary>Service locator.</summary>
    SRV = 33,

    /// <summary>EDNS(0) pseudo-record.</summary>
    OPT = 41,

    /// <summary>Certification authority authorization.</summary>
    CAA = 257,
}

/// <summary>DNS response codes (RFC 1035, 6895).</summary>
public enum DnsResponseCode : ushort
{
    /// <summary>No error.</summary>
    NoError = 0,

    /// <summary>Format error.</summary>
    FormatError = 1,

    /// <summary>Server failure.</summary>
    ServerFailure = 2,

    /// <summary>Name does not exist (NXDOMAIN).</summary>
    NameError = 3,

    /// <summary>Not implemented.</summary>
    NotImplemented = 4,

    /// <summary>Refused.</summary>
    Refused = 5,

    /// <summary>Name exists when it should not.</summary>
    YXDomain = 6,

    /// <summary>RR set exists when it should not.</summary>
    YXRRSet = 7,

    /// <summary>RR set that should exist does not.</summary>
    NXRRSet = 8,

    /// <summary>Not authoritative.</summary>
    NotAuthoritative = 9,

    /// <summary>Name not in zone.</summary>
    NotZone = 10,

    /// <summary>Unsupported EDNS version.</summary>
    BadVersion = 16,
}

/// <summary>Section of a DNS message.</summary>
public enum DnsSection : byte
{
    /// <summary>Answer.</summary>
    Answer = 1,

    /// <summary>Authority.</summary>
    Authority = 2,

    /// <summary>Additional.</summary>
    Additional = 3,
}

/// <summary>One resource record of a response.</summary>
/// <param name="Section">Section.</param>
/// <param name="Name">Owner name.</param>
/// <param name="Type">Type.</param>
/// <param name="Class">Class (1 = IN).</param>
/// <param name="TimeToLive">TTL in seconds.</param>
/// <param name="Data">Record data in presentation format.</param>
public sealed record DnsAnswer(DnsSection Section, string Name, DnsRecordType Type, ushort Class, uint TimeToLive, string Data)
{
    /// <summary>Type mnemonic ("A", "TYPE65").</summary>
    public string TypeText => DnsWire.TypeName(Type);

    /// <summary>TTL text.</summary>
    public string TimeToLiveText => TimeToLive.ToString(CultureInfo.CurrentCulture) + " s";
}

/// <summary>A decoded DNS message.</summary>
internal sealed record DnsMessage(
    ushort Id,
    bool IsResponse,
    int Opcode,
    bool Authoritative,
    bool Truncated,
    bool RecursionDesired,
    bool RecursionAvailable,
    bool AuthenticData,
    DnsResponseCode ResponseCode,
    int QuestionCount,
    string QuestionName,
    DnsRecordType QuestionType,
    IReadOnlyList<DnsAnswer> Records,
    int? EdnsPayloadSize);

/// <summary>DNS wire format (RFC 1035) encoder and a strict, bounds-checked decoder.</summary>
internal static class DnsWire
{
    /// <summary>EDNS(0) UDP payload size advertised (DNS Flag Day 2020 recommendation).</summary>
    public const ushort EdnsPayloadSize = 1232;

    private const int HeaderSize = 12;
    private const int MaxNameLength = 255;
    private const int MaxCompressionJumps = 64;

    /// <summary>Validates a name and converts it to ASCII (IDNA); returns "" for the root.</summary>
    public static string ToAsciiName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string text = name.Trim();
        if (text.EndsWith('.'))
        {
            text = text[..^1];
        }

        if (text.Length == 0)
        {
            return string.Empty;
        }

        if (!System.Text.Ascii.IsValid(text))
        {
            try
            {
                text = new IdnMapping().GetAscii(text);
            }
            catch (ArgumentException ex)
            {
                throw new DiagnosticsException("This is not a valid domain name: " + ex.Message, ex);
            }
        }

        if (text.Length > 253)
        {
            throw new DiagnosticsException("The name is longer than 253 characters.");
        }

        foreach (string label in text.Split('.'))
        {
            if (label.Length is 0 or > 63)
            {
                throw new DiagnosticsException("Each part of a name must be 1 to 63 characters long.");
            }

            foreach (char c in label)
            {
                if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
                {
                    throw new DiagnosticsException($"The name contains an invalid character: '{c}'.");
                }
            }
        }

        return text.ToLowerInvariant();
    }

    /// <summary>Builds a recursive query with an EDNS(0) OPT record.</summary>
    public static byte[] EncodeQuery(ushort id, string asciiName, DnsRecordType type)
    {
        ArgumentNullException.ThrowIfNull(asciiName);
        string[] labels = asciiName.Length == 0 ? [] : asciiName.Split('.');
        int nameLength = 1 + labels.Sum(l => l.Length + 1);
        var buffer = new byte[HeaderSize + nameLength + 4 + 11];
        var span = buffer.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span, id);
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], 0x0100); // RD
        BinaryPrimitives.WriteUInt16BigEndian(span[4..], 1);      // QDCOUNT
        BinaryPrimitives.WriteUInt16BigEndian(span[10..], 1);     // ARCOUNT (OPT)
        int pos = HeaderSize;
        foreach (string label in labels)
        {
            buffer[pos++] = (byte)label.Length;
            pos += Encoding.ASCII.GetBytes(label, span[pos..]);
        }

        buffer[pos++] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(span[pos..], (ushort)type);
        pos += 2;
        BinaryPrimitives.WriteUInt16BigEndian(span[pos..], 1); // IN
        pos += 2;

        // OPT pseudo-RR: root owner, TYPE 41, CLASS = UDP payload size, TTL = 0 (no extended RCODE, version 0), RDLEN 0.
        buffer[pos++] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(span[pos..], (ushort)DnsRecordType.OPT);
        pos += 2;
        BinaryPrimitives.WriteUInt16BigEndian(span[pos..], EdnsPayloadSize);
        return buffer;
    }

    /// <summary>Decodes a message; throws <see cref="DiagnosticsException"/> when it is malformed.</summary>
    public static DnsMessage Decode(ReadOnlySpan<byte> message)
    {
        if (message.Length < HeaderSize)
        {
            throw Malformed("the message is shorter than the 12-byte header");
        }

        ushort id = BinaryPrimitives.ReadUInt16BigEndian(message);
        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(message[2..]);
        int questions = BinaryPrimitives.ReadUInt16BigEndian(message[4..]);
        int answers = BinaryPrimitives.ReadUInt16BigEndian(message[6..]);
        int authority = BinaryPrimitives.ReadUInt16BigEndian(message[8..]);
        int additional = BinaryPrimitives.ReadUInt16BigEndian(message[10..]);
        if ((long)answers + authority + additional > message.Length / 11)
        {
            throw Malformed("record counts exceed the message size");
        }

        int pos = HeaderSize;
        string questionName = string.Empty;
        DnsRecordType questionType = 0;
        for (int i = 0; i < questions; i++)
        {
            string name = ReadName(message, ref pos);
            Need(message, pos, 4);
            if (i == 0)
            {
                questionName = name;
                questionType = (DnsRecordType)BinaryPrimitives.ReadUInt16BigEndian(message[pos..]);
            }

            pos += 4;
        }

        var records = new List<DnsAnswer>(answers + authority + additional);
        int? edns = null;
        int extendedRcode = 0;
        ReadSection(message, ref pos, answers, DnsSection.Answer, records, ref edns, ref extendedRcode);
        ReadSection(message, ref pos, authority, DnsSection.Authority, records, ref edns, ref extendedRcode);
        ReadSection(message, ref pos, additional, DnsSection.Additional, records, ref edns, ref extendedRcode);

        int rcode = (extendedRcode << 4) | (flags & 0xF);
        return new DnsMessage(
            id,
            (flags & 0x8000) != 0,
            (flags >> 11) & 0xF,
            (flags & 0x0400) != 0,
            (flags & 0x0200) != 0,
            (flags & 0x0100) != 0,
            (flags & 0x0080) != 0,
            (flags & 0x0020) != 0,
            (DnsResponseCode)rcode,
            questions,
            questionName,
            questionType,
            records,
            edns);
    }

    /// <summary>Type mnemonic.</summary>
    public static string TypeName(DnsRecordType type) =>
        Enum.IsDefined(type) ? type.ToString() : "TYPE" + ((ushort)type).ToString(CultureInfo.InvariantCulture);

    private static void ReadSection(ReadOnlySpan<byte> message, ref int pos, int count, DnsSection section, List<DnsAnswer> records, ref int? edns, ref int extendedRcode)
    {
        for (int i = 0; i < count; i++)
        {
            string name = ReadName(message, ref pos);
            Need(message, pos, 10);
            var type = (DnsRecordType)BinaryPrimitives.ReadUInt16BigEndian(message[pos..]);
            ushort cls = BinaryPrimitives.ReadUInt16BigEndian(message[(pos + 2)..]);
            uint ttl = BinaryPrimitives.ReadUInt32BigEndian(message[(pos + 4)..]);
            int length = BinaryPrimitives.ReadUInt16BigEndian(message[(pos + 8)..]);
            pos += 10;
            Need(message, pos, length);
            if (type == DnsRecordType.OPT)
            {
                edns = cls;
                extendedRcode = (int)(ttl >> 24);
            }
            else
            {
                records.Add(new DnsAnswer(section, name, type, cls, ttl, FormatData(message, pos, length, type)));
            }

            pos += length;
        }
    }

    private static string FormatData(ReadOnlySpan<byte> message, int start, int length, DnsRecordType type)
    {
        var data = message.Slice(start, length);
        int end = start + length;
        int p = start;
        switch (type)
        {
            case DnsRecordType.A:
                if (length != 4)
                {
                    throw Malformed($"A record with {length} bytes");
                }

                return new IPAddress(data).ToString();
            case DnsRecordType.AAAA:
                if (length != 16)
                {
                    throw Malformed($"AAAA record with {length} bytes");
                }

                return new IPAddress(data).ToString();
            case DnsRecordType.CNAME or DnsRecordType.NS or DnsRecordType.PTR:
            {
                string target = ReadName(message, ref p);
                Within(p, end);
                return target;
            }

            case DnsRecordType.MX:
            {
                NeedWithin(p, 2, end);
                ushort preference = BinaryPrimitives.ReadUInt16BigEndian(message[p..]);
                p += 2;
                string exchange = ReadName(message, ref p);
                Within(p, end);
                return string.Create(CultureInfo.InvariantCulture, $"{preference} {exchange}");
            }

            case DnsRecordType.TXT:
            {
                var text = new StringBuilder();
                while (p < end)
                {
                    int len = message[p++];
                    NeedWithin(p, len, end);
                    if (text.Length > 0)
                    {
                        text.Append(' ');
                    }

                    text.Append('"');
                    AppendEscaped(text, message.Slice(p, len), quoted: true);
                    text.Append('"');
                    p += len;
                }

                return text.ToString();
            }

            case DnsRecordType.SOA:
            {
                string primary = ReadName(message, ref p);
                string mailbox = ReadName(message, ref p);
                NeedWithin(p, 20, end);
                uint serial = BinaryPrimitives.ReadUInt32BigEndian(message[p..]);
                uint refresh = BinaryPrimitives.ReadUInt32BigEndian(message[(p + 4)..]);
                uint retry = BinaryPrimitives.ReadUInt32BigEndian(message[(p + 8)..]);
                uint expire = BinaryPrimitives.ReadUInt32BigEndian(message[(p + 12)..]);
                uint minimum = BinaryPrimitives.ReadUInt32BigEndian(message[(p + 16)..]);
                return string.Create(CultureInfo.InvariantCulture, $"{primary} {mailbox} {serial} {refresh} {retry} {expire} {minimum}");
            }

            case DnsRecordType.SRV:
            {
                NeedWithin(p, 6, end);
                ushort priority = BinaryPrimitives.ReadUInt16BigEndian(message[p..]);
                ushort weight = BinaryPrimitives.ReadUInt16BigEndian(message[(p + 2)..]);
                ushort port = BinaryPrimitives.ReadUInt16BigEndian(message[(p + 4)..]);
                p += 6;
                string target = ReadName(message, ref p);
                Within(p, end);
                return string.Create(CultureInfo.InvariantCulture, $"{priority} {weight} {port} {target}");
            }

            case DnsRecordType.CAA:
            {
                NeedWithin(p, 2, end);
                byte flags = message[p];
                int tagLength = message[p + 1];
                p += 2;
                NeedWithin(p, tagLength, end);
                string tag = Encoding.ASCII.GetString(message.Slice(p, tagLength));
                p += tagLength;
                var value = new StringBuilder();
                AppendEscaped(value, message[p..end], quoted: true);
                return string.Create(CultureInfo.InvariantCulture, $"{flags} {tag} \"{value}\"");
            }

            default:
                return string.Create(CultureInfo.InvariantCulture, $"\\# {length} {Convert.ToHexString(data)}");
        }
    }

    /// <summary>Reads a possibly compressed name; <paramref name="pos"/> ends after the name in the original location.</summary>
    internal static string ReadName(ReadOnlySpan<byte> message, ref int pos)
    {
        var name = new StringBuilder();
        int p = pos;
        bool jumped = false;
        int jumps = 0;
        int wireLength = 0;
        while (true)
        {
            if (p >= message.Length)
            {
                throw Malformed("a name runs past the end of the message");
            }

            byte length = message[p];
            if ((length & 0xC0) == 0xC0)
            {
                if (p + 1 >= message.Length)
                {
                    throw Malformed("truncated compression pointer");
                }

                int target = ((length & 0x3F) << 8) | message[p + 1];
                if (!jumped)
                {
                    pos = p + 2;
                    jumped = true;
                }

                if (++jumps > MaxCompressionJumps || target >= message.Length)
                {
                    throw Malformed("invalid or looping compression pointer");
                }

                p = target;
                continue;
            }

            if ((length & 0xC0) != 0)
            {
                throw Malformed("unsupported label type");
            }

            if (length == 0)
            {
                if (!jumped)
                {
                    pos = p + 1;
                }

                break;
            }

            if (p + 1 + length > message.Length)
            {
                throw Malformed("a label runs past the end of the message");
            }

            wireLength += length + 1;
            if (wireLength > MaxNameLength)
            {
                throw Malformed("a name is longer than 255 bytes");
            }

            if (name.Length > 0)
            {
                name.Append('.');
            }

            AppendEscaped(name, message.Slice(p + 1, length), quoted: false);
            p += 1 + length;
        }

        return name.Length == 0 ? "." : name.ToString();
    }

    private static void AppendEscaped(StringBuilder builder, ReadOnlySpan<byte> bytes, bool quoted)
    {
        foreach (byte b in bytes)
        {
            if (b is < 0x20 or > 0x7E)
            {
                builder.Append('\\').Append(b.ToString("000", CultureInfo.InvariantCulture));
            }
            else if (b == '\\' || (quoted && b == '"') || (!quoted && b == '.'))
            {
                builder.Append('\\').Append((char)b);
            }
            else
            {
                builder.Append((char)b);
            }
        }
    }

    private static void Need(ReadOnlySpan<byte> message, int pos, int count)
    {
        if (pos + count > message.Length)
        {
            throw Malformed("the message is truncated");
        }
    }

    private static void NeedWithin(int pos, int count, int end)
    {
        if (pos + count > end)
        {
            throw Malformed("record data is shorter than its fields");
        }
    }

    private static void Within(int pos, int end)
    {
        if (pos > end)
        {
            throw Malformed("a name runs past the end of its record");
        }
    }

    private static DiagnosticsException Malformed(string detail) => new("The DNS response is malformed: " + detail + ".");
}
