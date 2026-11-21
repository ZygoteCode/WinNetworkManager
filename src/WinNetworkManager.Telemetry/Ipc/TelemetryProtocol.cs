using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using WinNetworkManager.Core.Networking;
using WinNetworkManager.Core.Processes;
using WinNetworkManager.Core.Traffic;
using WinNetworkManager.Telemetry.Model;

namespace WinNetworkManager.Telemetry.Ipc;

/// <summary>Message types of the collector pipe protocol.</summary>
public enum MessageType : byte
{
    /// <summary>Server → client: protocol version and timeline parameters.</summary>
    Hello = 1,

    /// <summary>Server → client: one telemetry frame.</summary>
    Frame = 2,

    /// <summary>Server → client: collector status.</summary>
    Status = 3,

    /// <summary>Server → client: log record.</summary>
    Log = 4,

    /// <summary>Client → server: stop and exit.</summary>
    Shutdown = 10,
}

/// <summary>Hello message content.</summary>
/// <param name="ProtocolVersion">Protocol version.</param>
/// <param name="ServerPid">PID of the helper.</param>
/// <param name="TimestampFrequency">QPC frequency of the helper (must equal the client's).</param>
/// <param name="BucketTicks">Bucket duration in QPC ticks.</param>
/// <param name="SessionName">ETW session name.</param>
public sealed record HelloMessage(int ProtocolVersion, int ServerPid, long TimestampFrequency, long BucketTicks, string SessionName);

/// <summary>Log record forwarded by the helper.</summary>
/// <param name="Level">0 trace … 5 critical (Microsoft.Extensions.Logging.LogLevel values).</param>
/// <param name="Message">Rendered message.</param>
public sealed record LogMessage(int Level, string Message);

/// <summary>
/// Length-prefixed binary protocol between the elevated collector helper and the UI:
/// <c>[int32 length][byte type][payload]</c>, little-endian. Every read is bounds-checked; malformed input throws
/// <see cref="InvalidDataException"/> and the connection is closed.
/// </summary>
public static class TelemetryProtocol
{
    /// <summary>Current protocol version. Client and helper ship together, so versions must match exactly.</summary>
    public const int Version = 1;

    /// <summary>Upper bound for one message.</summary>
    public const int MaxMessageBytes = 64 * 1024 * 1024;

    private const int MaxStringBytes = 64 * 1024;
    private const int MaxItems = 1_000_000;

    /// <summary>Writes a message.</summary>
    public static async ValueTask WriteAsync(Stream stream, MessageType type, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] header = new byte[5];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length + 1);
        header[4] = (byte)type;
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (!payload.IsEmpty)
        {
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one message; returns null at end of stream.</summary>
    public static async ValueTask<(MessageType Type, byte[] Payload)?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] header = new byte[5];
        if (!await ReadExactlyOrEndAsync(stream, header, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 1 || length > MaxMessageBytes)
        {
            throw new InvalidDataException($"Invalid message length {length}.");
        }

        var payload = new byte[length - 1];
        if (payload.Length > 0 && !await ReadExactlyOrEndAsync(stream, payload, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("Truncated message.");
        }

        return ((MessageType)header[4], payload);
    }

    /// <summary>Encodes a hello message.</summary>
    public static byte[] EncodeHello(HelloMessage hello)
    {
        ArgumentNullException.ThrowIfNull(hello);
        var w = new ProtocolWriter();
        w.Int32(hello.ProtocolVersion);
        w.Int32(hello.ServerPid);
        w.Int64(hello.TimestampFrequency);
        w.Int64(hello.BucketTicks);
        w.String(hello.SessionName);
        return w.ToArray();
    }

    /// <summary>Decodes a hello message.</summary>
    public static HelloMessage DecodeHello(ReadOnlySpan<byte> payload)
    {
        var r = new ProtocolReader(payload);
        return new HelloMessage(r.Int32(), r.Int32(), r.Int64(), r.Int64(), r.String() ?? string.Empty);
    }

    /// <summary>Encodes a log message.</summary>
    public static byte[] EncodeLog(LogMessage log)
    {
        ArgumentNullException.ThrowIfNull(log);
        var w = new ProtocolWriter();
        w.Int32(log.Level);
        w.String(log.Message);
        return w.ToArray();
    }

    /// <summary>Decodes a log message.</summary>
    public static LogMessage DecodeLog(ReadOnlySpan<byte> payload)
    {
        var r = new ProtocolReader(payload);
        return new LogMessage(r.Int32(), r.String() ?? string.Empty);
    }

    /// <summary>Encodes a status message.</summary>
    public static byte[] EncodeStatus(CollectorStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var w = new ProtocolWriter();
        w.Byte((byte)status.State);
        w.String(status.SessionName);
        w.Double(status.EventsPerSecond);
        w.Int64(status.TotalEvents);
        w.Int64(status.TotalEventsLost);
        w.Int64(status.TotalLateEvents);
        w.Int64(status.TotalDecodeFailures);
        w.Int32(status.Restarts);
        w.Bool(status.UsesMillisecondFlushTimer);
        w.String(status.LastError);
        w.Bool(status.IsAccessDenied);
        return w.ToArray();
    }

    /// <summary>Decodes a status message.</summary>
    public static CollectorStatus DecodeStatus(ReadOnlySpan<byte> payload)
    {
        var r = new ProtocolReader(payload);
        byte state = r.Byte();
        if (!Enum.IsDefined((CollectorState)state))
        {
            throw new InvalidDataException("Unknown collector state.");
        }

        return new CollectorStatus((CollectorState)state, r.String() ?? string.Empty, r.Double(), r.Int64(), r.Int64(), r.Int64(), r.Int64(), r.Int32(), r.Bool(), r.String(), r.Bool());
    }

    /// <summary>Encodes a frame.</summary>
    public static byte[] EncodeFrame(TelemetryFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var w = new ProtocolWriter();
        w.Int64(frame.BucketIndex);
        w.Int64(frame.StartTimestamp);
        w.Int64(frame.EndTimestamp);

        w.Int32(frame.Processes.Count);
        foreach (var p in frame.Processes)
        {
            w.ProcessKey(p.Process);
            w.ProtocolBytes(p.Counters.External);
            w.ProtocolBytes(p.Counters.Loopback);
            w.Int64(p.Counters.TcpRetransmittedBytes);
            w.Int64(p.Counters.TcpRetransmissions);
        }

        w.Int32(frame.Flows.Count);
        foreach (var f in frame.Flows)
        {
            w.Byte((byte)f.Key.Protocol);
            w.ProcessKey(f.Key.Process);
            w.Endpoint(f.Key.Local);
            w.Endpoint(f.Key.Remote);
            w.Int64(f.Counters.ReceivedBytes);
            w.Int64(f.Counters.SentBytes);
            w.Int32(f.Counters.ReceiveEvents);
            w.Int32(f.Counters.SendEvents);
            w.Int64(f.Counters.RetransmittedBytes);
            w.Int32(f.Counters.Retransmissions);
        }

        w.Int32(frame.ProcessEvents.Count);
        foreach (var e in frame.ProcessEvents)
        {
            w.Byte((byte)e.Kind);
            w.Int64(e.Timestamp);
            w.ProcessKey(e.Process);
            w.Int32(e.ParentPid);
            w.Int32(e.SessionId);
            w.String(e.ImageName);
            w.Bool(e.ExitCode.HasValue);
            w.Int32(e.ExitCode ?? 0);
            w.Int64((long)e.SequenceNumber);
        }

        w.Int32(frame.Health.Events);
        w.Int32(frame.Health.LateEvents);
        w.Int64(frame.Health.EventsLost);
        w.Int32(frame.Health.DroppedFlows);
        w.Int32(frame.Health.DecodeFailures);
        return w.ToArray();
    }

    /// <summary>Decodes a frame.</summary>
    public static TelemetryFrame DecodeFrame(ReadOnlySpan<byte> payload)
    {
        var r = new ProtocolReader(payload);
        long index = r.Int64();
        long start = r.Int64();
        long end = r.Int64();

        int processCount = r.Count();
        var processes = new ProcessTraffic[processCount];
        for (int i = 0; i < processCount; i++)
        {
            var key = r.ProcessKey();
            var external = r.ProtocolBytes();
            var loopback = r.ProtocolBytes();
            processes[i] = new ProcessTraffic(key, new TrafficCounters(external, loopback, r.Int64(), r.Int64()));
        }

        int flowCount = r.Count();
        var flows = new FlowTraffic[flowCount];
        for (int i = 0; i < flowCount; i++)
        {
            byte protocol = r.Byte();
            if (protocol is not ((byte)TransportProtocol.Tcp or (byte)TransportProtocol.Udp))
            {
                throw new InvalidDataException("Unknown protocol.");
            }

            var key = new FlowKey((TransportProtocol)protocol, r.ProcessKey(), r.Endpoint(), r.Endpoint());
            var counters = new FlowCounters
            {
                ReceivedBytes = r.Int64(),
                SentBytes = r.Int64(),
                ReceiveEvents = r.Int32(),
                SendEvents = r.Int32(),
                RetransmittedBytes = r.Int64(),
                Retransmissions = r.Int32(),
            };
            flows[i] = new FlowTraffic(key, counters);
        }

        int eventCount = r.Count();
        var events = new ProcessLifecycleEvent[eventCount];
        for (int i = 0; i < eventCount; i++)
        {
            byte kind = r.Byte();
            if (kind is not ((byte)ProcessLifecycleKind.Started or (byte)ProcessLifecycleKind.Exited))
            {
                throw new InvalidDataException("Unknown lifecycle kind.");
            }

            long ts = r.Int64();
            var key = r.ProcessKey();
            int parent = r.Int32();
            int session = r.Int32();
            string? image = r.String();
            bool hasExit = r.Bool();
            int exit = r.Int32();
            ulong sequence = (ulong)r.Int64();
            events[i] = new ProcessLifecycleEvent((ProcessLifecycleKind)kind, ts, key, parent, session, image, hasExit ? exit : null, sequence);
        }

        var health = new FrameHealth(r.Int32(), r.Int32(), r.Int64(), r.Int32(), r.Int32());
        r.EnsureEnd();
        return new TelemetryFrame
        {
            BucketIndex = index,
            StartTimestamp = start,
            EndTimestamp = end,
            Processes = processes,
            Flows = flows,
            ProcessEvents = events,
            Health = health,
        };
    }

    private static async ValueTask<bool> ReadExactlyOrEndAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                if (read == 0)
                {
                    return false;
                }

                throw new InvalidDataException("Unexpected end of stream.");
            }

            read += n;
        }

        return true;
    }

    private sealed class ProtocolWriter
    {
        private readonly ArrayBufferWriter<byte> _buffer = new(256);

        public byte[] ToArray() => _buffer.WrittenSpan.ToArray();

        public void Byte(byte value) => WriteByte(value);

        public void Bool(bool value)
        {
            _buffer.GetSpan(1)[0] = value ? (byte)1 : (byte)0;
            _buffer.Advance(1);
        }

        public void Int32(int value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(_buffer.GetSpan(4), value);
            _buffer.Advance(4);
        }

        public void Int64(long value)
        {
            BinaryPrimitives.WriteInt64LittleEndian(_buffer.GetSpan(8), value);
            _buffer.Advance(8);
        }

        public void Double(double value)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(_buffer.GetSpan(8), value);
            _buffer.Advance(8);
        }

        public void String(string? value)
        {
            if (value is null)
            {
                Int32(-1);
                return;
            }

            int count = Encoding.UTF8.GetByteCount(value);
            if (count > MaxStringBytes)
            {
                value = value[..Math.Min(value.Length, MaxStringBytes / 4)];
                count = Encoding.UTF8.GetByteCount(value);
            }

            Int32(count);
            Encoding.UTF8.GetBytes(value, _buffer.GetSpan(count));
            _buffer.Advance(count);
        }

        public void ProcessKey(ProcessKey key)
        {
            Int32(key.Pid);
            Int64(key.CreateTimeTicks);
        }

        public void ProtocolBytes(ProtocolBytes bytes)
        {
            Int64(bytes.TcpReceived);
            Int64(bytes.TcpSent);
            Int64(bytes.UdpReceived);
            Int64(bytes.UdpSent);
        }

        public void Endpoint(IpEndpointValue endpoint)
        {
            var address = endpoint.Address;
            WriteByte((byte)address.Version);
            if (address.IsIPv4)
            {
                Int32(unchecked((int)address.IPv4HostOrder));
            }
            else if (address.IsIPv6)
            {
                Int64(unchecked((long)address.High));
                Int64(unchecked((long)address.Low));
                Int32(unchecked((int)address.ScopeId));
            }

            BinaryPrimitives.WriteUInt16LittleEndian(_buffer.GetSpan(2), endpoint.Port);
            _buffer.Advance(2);
        }

        private void WriteByte(byte value)
        {
            _buffer.GetSpan(1)[0] = value;
            _buffer.Advance(1);
        }
    }

    private ref struct ProtocolReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _position;

        public ProtocolReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _position = 0;
        }

        public byte Byte() => Take(1)[0];

        public bool Bool() => Take(1)[0] != 0;

        public int Int32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

        public long Int64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

        public double Double() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));

        public int Count()
        {
            int count = Int32();
            if (count < 0 || count > MaxItems)
            {
                throw new InvalidDataException($"Invalid item count {count}.");
            }

            return count;
        }

        public string? String()
        {
            int length = Int32();
            if (length == -1)
            {
                return null;
            }

            if (length < 0 || length > MaxStringBytes)
            {
                throw new InvalidDataException($"Invalid string length {length}.");
            }

            return Encoding.UTF8.GetString(Take(length));
        }

        public ProcessKey ProcessKey() => new(Int32(), Int64());

        public ProtocolBytes ProtocolBytes() => new(Int64(), Int64(), Int64(), Int64());

        public IpEndpointValue Endpoint()
        {
            byte version = Byte();
            IpAddressValue address = version switch
            {
                0 => default,
                4 => IpAddressValue.FromIPv4HostOrder(unchecked((uint)Int32())),
                6 => IpAddressValue.FromIPv6(unchecked((ulong)Int64()), unchecked((ulong)Int64()), unchecked((uint)Int32())),
                _ => throw new InvalidDataException($"Invalid address version {version}."),
            };
            ushort port = BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
            return new IpEndpointValue(address, port);
        }

        public readonly void EnsureEnd()
        {
            if (_position != _data.Length)
            {
                throw new InvalidDataException("Trailing bytes in message.");
            }
        }

        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || _position + count > _data.Length)
            {
                throw new InvalidDataException("Message truncated.");
            }

            var slice = _data.Slice(_position, count);
            _position += count;
            return slice;
        }
    }
}
