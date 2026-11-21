using System.Text;

namespace WinNetworkManager.Diagnostics.Tests;

/// <summary>Writes DNS messages byte by byte for decoder tests and fake servers.</summary>
internal sealed class DnsMessageBuilder
{
    private readonly List<byte> _bytes = [];

    public int Position => _bytes.Count;

    public DnsMessageBuilder U8(int value)
    {
        _bytes.Add((byte)value);
        return this;
    }

    public DnsMessageBuilder U16(int value) => U8(value >> 8).U8(value & 0xFF);

    public DnsMessageBuilder U32(uint value) => U16((int)(value >> 16)).U16((int)(value & 0xFFFF));

    public DnsMessageBuilder Bytes(params byte[] bytes)
    {
        _bytes.AddRange(bytes);
        return this;
    }

    public DnsMessageBuilder Ascii(string text) => Bytes(Encoding.ASCII.GetBytes(text));

    /// <summary>Labels without the terminating zero (to be followed by a pointer).</summary>
    public DnsMessageBuilder Labels(string name)
    {
        foreach (string label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            U8(label.Length).Ascii(label);
        }

        return this;
    }

    public DnsMessageBuilder Name(string name) => Labels(name).U8(0);

    public DnsMessageBuilder Pointer(int offset) => U16(0xC000 | offset);

    public DnsMessageBuilder Header(int id, int flags, int questions, int answers, int authority, int additional) =>
        U16(id).U16(flags).U16(questions).U16(answers).U16(authority).U16(additional);

    /// <summary>Writes a resource record; <paramref name="owner"/> writes the owner name, <paramref name="data"/> the RDATA.</summary>
    public DnsMessageBuilder Record(Action<DnsMessageBuilder> owner, int type, uint ttl, Action<DnsMessageBuilder> data, int cls = 1)
    {
        owner(this);
        U16(type).U16(cls).U32(ttl);
        int lengthAt = Position;
        U16(0);
        int start = Position;
        data(this);
        int length = Position - start;
        _bytes[lengthAt] = (byte)(length >> 8);
        _bytes[lengthAt + 1] = (byte)length;
        return this;
    }

    public byte[] ToArray() => [.. _bytes];

    /// <summary>Builds an answer to <paramref name="query"/> (same ID and question) with the given flags and A records.</summary>
    public static byte[] AnswerTo(byte[] query, int flags, params string[] addresses)
    {
        int end = 12;
        while (query[end] != 0)
        {
            end += query[end] + 1;
        }

        end += 1 + 4; // terminating zero + QTYPE + QCLASS
        var builder = new DnsMessageBuilder()
            .Bytes(query[0], query[1])
            .U16(flags).U16(1).U16(addresses.Length).U16(0).U16(0)
            .Bytes(query[12..end]);
        foreach (string address in addresses)
        {
            builder.Record(b => b.Pointer(12), 1, 60, b => b.Bytes(System.Net.IPAddress.Parse(address).GetAddressBytes()));
        }

        return builder.ToArray();
    }
}
