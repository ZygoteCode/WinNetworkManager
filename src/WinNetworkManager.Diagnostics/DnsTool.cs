using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using WinNetworkManager.Core.Errors;

namespace WinNetworkManager.Diagnostics;

/// <summary>Result of a query sent directly to one DNS server.</summary>
/// <param name="Name">Name queried (ASCII form, "." for the root).</param>
/// <param name="Type">Type queried.</param>
/// <param name="Server">Server that answered.</param>
/// <param name="Transport">"UDP", or "TCP" when the UDP answer was truncated.</param>
/// <param name="ElapsedMs">Time from sending the query to receiving the complete answer.</param>
/// <param name="ResponseBytes">Size of the answer.</param>
/// <param name="ResponseCode">Response code.</param>
/// <param name="Authoritative">AA flag: the server is authoritative for the name.</param>
/// <param name="Truncated">TC flag of the final answer.</param>
/// <param name="RecursionAvailable">RA flag.</param>
/// <param name="AuthenticData">AD flag: the server claims it validated the answer with DNSSEC (only meaningful if you trust the server and the path to it).</param>
/// <param name="Records">Records of all sections.</param>
public sealed record DnsQueryResult(
    string Name,
    DnsRecordType Type,
    IPEndPoint Server,
    string Transport,
    double ElapsedMs,
    int ResponseBytes,
    DnsResponseCode ResponseCode,
    bool Authoritative,
    bool Truncated,
    bool RecursionAvailable,
    bool AuthenticData,
    IReadOnlyList<DnsAnswer> Records)
{
    /// <summary>Answer-section records.</summary>
    public IEnumerable<DnsAnswer> Answers => Records.Where(r => r.Section == DnsSection.Answer);

    /// <summary>Response code text.</summary>
    public string ResponseCodeText => DnsTool.Describe(ResponseCode);
}

/// <summary>Result of a lookup through the Windows resolver.</summary>
/// <param name="Name">Name looked up.</param>
/// <param name="Addresses">Addresses (empty on failure).</param>
/// <param name="ElapsedMs">Time taken (near zero when answered from the cache or the hosts file).</param>
/// <param name="Error">Failure text, or null.</param>
public sealed record SystemResolveResult(string Name, IReadOnlyList<IPAddress> Addresses, double ElapsedMs, string? Error);

/// <summary>A DNS server configured on an interface.</summary>
/// <param name="Address">Server address.</param>
/// <param name="InterfaceName">Interface it is configured on.</param>
public sealed record DnsServerInfo(IPAddress Address, string InterfaceName)
{
    /// <summary>Display text.</summary>
    public string Display => $"{Address} ({InterfaceName})";
}

/// <summary>DNS diagnostics: system resolver lookups, direct queries to a chosen server, reverse names.</summary>
public static class DnsTool
{
    private const string HexDigits = "0123456789abcdef";

    /// <summary>Resolves <paramref name="name"/> exactly as applications do (hosts file, DNS cache, configured DNS servers, LLMNR/mDNS).</summary>
    public static async Task<SystemResolveResult> ResolveWithSystemAsync(string name, CancellationToken cancellationToken)
    {
        string ascii = DnsWire.ToAsciiName(name);
        if (ascii.Length == 0)
        {
            throw new DiagnosticsException("Enter a name to resolve.");
        }

        long started = Stopwatch.GetTimestamp();
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(ascii, cancellationToken).ConfigureAwait(false);
            return new SystemResolveResult(ascii, addresses, Stopwatch.GetElapsedTime(started).TotalMilliseconds, addresses.Length == 0 ? "No addresses." : null);
        }
        catch (SocketException ex)
        {
            return new SystemResolveResult(ascii, [], Stopwatch.GetElapsedTime(started).TotalMilliseconds, DiagnosticTarget.DescribeResolverError(ascii, ex));
        }
    }

    /// <summary>Reverse-resolves <paramref name="address"/> through the Windows resolver; null when no name is registered.</summary>
    public static async Task<(string? HostName, double ElapsedMs)> ReverseWithSystemAsync(IPAddress address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        long started = Stopwatch.GetTimestamp();
        try
        {
            var entry = await Dns.GetHostEntryAsync(address.ToString(), cancellationToken).ConfigureAwait(false);
            string? name = string.IsNullOrEmpty(entry.HostName) || entry.HostName == address.ToString() ? null : entry.HostName;
            return (name, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (SocketException)
        {
            return (null, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    /// <summary>
    /// Sends one recursive query for <paramref name="name"/>/<paramref name="type"/> to <paramref name="server"/> over UDP
    /// (EDNS(0), 1232-byte payload), retrying over TCP when the answer is truncated. Answers whose ID or question do not
    /// match the query are ignored.
    /// </summary>
    /// <exception cref="DiagnosticsException">No usable answer (timeout, unreachable server, malformed response).</exception>
    public static async Task<DnsQueryResult> QueryAsync(string name, DnsRecordType type, IPAddress server, TimeSpan timeout, CancellationToken cancellationToken, int port = 53)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        if (type == DnsRecordType.OPT)
        {
            throw new DiagnosticsException("OPT is not a queryable record type.");
        }

        string ascii = DnsWire.ToAsciiName(name);
        ushort id = (ushort)RandomNumberGenerator.GetInt32(0, 65536);
        byte[] query = DnsWire.EncodeQuery(id, ascii, type);
        var endpoint = new IPEndPoint(server, port);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        long started = Stopwatch.GetTimestamp();
        string transport = "UDP";
        try
        {
            var (bytes, message) = await ExchangeUdpAsync(query, endpoint, id, ascii, type, timeoutSource.Token).ConfigureAwait(false);
            if (message.Truncated)
            {
                transport = "TCP";
                (bytes, message) = await ExchangeTcpAsync(query, endpoint, id, ascii, type, timeoutSource.Token).ConfigureAwait(false);
            }

            return new DnsQueryResult(
                ascii.Length == 0 ? "." : ascii,
                type,
                endpoint,
                transport,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                bytes,
                message.ResponseCode,
                message.Authoritative,
                message.Truncated,
                message.RecursionAvailable,
                message.AuthenticData,
                message.Records);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DiagnosticsException(string.Create(CultureInfo.CurrentCulture, $"No answer from {endpoint} over {transport} within {timeout.TotalMilliseconds:0} ms."));
        }
        catch (SocketException ex)
        {
            throw new DiagnosticsException(ex.SocketErrorCode == SocketError.ConnectionReset || ex.SocketErrorCode == SocketError.ConnectionRefused
                ? $"{endpoint} is not accepting DNS queries over {transport} (port unreachable or connection refused)."
                : $"The query to {endpoint} failed: {ErrorText.Describe(ex)}", ex);
        }
        catch (IOException ex)
        {
            throw new DiagnosticsException($"The {transport} exchange with {endpoint} failed: {ErrorText.Describe(ex)}", ex);
        }
    }

    /// <summary>Reverse-lookup name: "4.3.2.1.in-addr.arpa" or the nibble form under "ip6.arpa".</summary>
    public static string ReverseName(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes[3]}.{bytes[2]}.{bytes[1]}.{bytes[0]}.in-addr.arpa");
        }

        var name = new StringBuilder(72);
        for (int i = bytes.Length - 1; i >= 0; i--)
        {
            name.Append(HexDigits[bytes[i] & 0xF]).Append('.').Append(HexDigits[bytes[i] >> 4]).Append('.');
        }

        return name.Append("ip6.arpa").ToString();
    }

    /// <summary>Explains a response code.</summary>
    public static string Describe(DnsResponseCode code) => code switch
    {
        DnsResponseCode.NoError => "NOERROR: the query succeeded",
        DnsResponseCode.FormatError => "FORMERR: the server could not understand the query",
        DnsResponseCode.ServerFailure => "SERVFAIL: the server failed to answer (upstream problem or failed DNSSEC validation)",
        DnsResponseCode.NameError => "NXDOMAIN: the name does not exist",
        DnsResponseCode.NotImplemented => "NOTIMP: the server does not support this query",
        DnsResponseCode.Refused => "REFUSED: the server refuses to answer (for example it does not resolve for your network)",
        DnsResponseCode.NotAuthoritative => "NOTAUTH: the server is not authoritative for the zone",
        DnsResponseCode.BadVersion => "BADVERS: unsupported EDNS version",
        _ => "RCODE " + ((int)code).ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>DNS servers configured on interfaces that are up (in interface order, without duplicates).</summary>
    public static IReadOnlyList<DnsServerInfo> ConfiguredServers()
    {
        var servers = new List<DnsServerInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            IPAddressCollection addresses;
            try
            {
                addresses = nic.GetIPProperties().DnsAddresses;
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            foreach (var address in addresses)
            {
                if (servers.TrueForAll(s => !s.Address.Equals(address)))
                {
                    servers.Add(new DnsServerInfo(address, nic.Name));
                }
            }
        }

        return servers;
    }

    private static async Task<(int Bytes, DnsMessage Message)> ExchangeUdpAsync(byte[] query, IPEndPoint endpoint, ushort id, string name, DnsRecordType type, CancellationToken cancellationToken)
    {
        using var socket = new Socket(endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        await socket.SendAsync(query, SocketFlags.None, cancellationToken).ConfigureAwait(false);
        var buffer = new byte[65535];
        while (true)
        {
            int received = await socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);
            var message = TryAccept(buffer.AsSpan(0, received), id, name, type);
            if (message is not null)
            {
                return (received, message);
            }

            // Not an answer to this query (late answer to an earlier query, or spoofed): keep waiting.
        }
    }

    private static async Task<(int Bytes, DnsMessage Message)> ExchangeTcpAsync(byte[] query, IPEndPoint endpoint, ushort id, string name, DnsRecordType type, CancellationToken cancellationToken)
    {
        using var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        var prefix = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(prefix, (ushort)query.Length);
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(query, cancellationToken).ConfigureAwait(false);
        await stream.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadUInt16BigEndian(prefix);
        var response = new byte[length];
        await stream.ReadExactlyAsync(response, cancellationToken).ConfigureAwait(false);
        return (length, TryAccept(response, id, name, type) ?? throw new DiagnosticsException("The TCP answer does not match the query."));
    }

    /// <summary>Returns the decoded message when it answers this query, null when it belongs to something else.</summary>
    internal static DnsMessage? TryAccept(ReadOnlySpan<byte> response, ushort id, string name, DnsRecordType type)
    {
        if (response.Length < 12 || BinaryPrimitives.ReadUInt16BigEndian(response) != id || (response[2] & 0x80) == 0)
        {
            return null;
        }

        var message = DnsWire.Decode(response);
        if (message.QuestionCount == 0)
        {
            return message.ResponseCode != DnsResponseCode.NoError ? message : null; // some servers omit the question in error answers
        }

        string question = message.QuestionName == "." ? string.Empty : message.QuestionName;
        return string.Equals(question, name, StringComparison.OrdinalIgnoreCase) && message.QuestionType == type ? message : null;
    }
}
