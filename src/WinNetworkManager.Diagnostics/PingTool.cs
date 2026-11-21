using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using WinNetworkManager.Core.Errors;

namespace WinNetworkManager.Diagnostics;

/// <summary>Echo request parameters.</summary>
public sealed record PingSettings
{
    /// <summary>Number of requests; 0 = until cancelled.</summary>
    public int Count { get; init; } = 4;

    /// <summary>Time between the start of consecutive requests.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Time to wait for each reply.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>ICMP payload size in bytes (0–65500).</summary>
    public int PayloadSize { get; init; } = 32;

    /// <summary>Time to live / hop limit (1–255).</summary>
    public int Ttl { get; init; } = 128;

    /// <summary>Set the IPv4 "don't fragment" flag.</summary>
    public bool DontFragment { get; init; }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(Count);
        ArgumentOutOfRangeException.ThrowIfLessThan(Interval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(Timeout, TimeSpan.FromMilliseconds(1));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Timeout, TimeSpan.FromMinutes(1));
        ArgumentOutOfRangeException.ThrowIfNegative(PayloadSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(PayloadSize, 65500);
        ArgumentOutOfRangeException.ThrowIfLessThan(Ttl, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Ttl, 255);
    }
}

/// <summary>Result of one echo request.</summary>
/// <param name="Sequence">1-based sequence number.</param>
/// <param name="Timestamp">When the request was sent (UTC).</param>
/// <param name="Target">Destination.</param>
/// <param name="From">Address that answered (destination or a router reporting an error), when known.</param>
/// <param name="Status">ICMP status.</param>
/// <param name="RoundTripMs">
/// Round-trip time reported by Windows, in whole milliseconds (the ICMP API has 1 ms resolution: 0 means "less than 1 ms").
/// Null when no echo reply arrived.
/// </param>
/// <param name="Ttl">TTL of the reply (IPv4 only; Windows does not report the IPv6 hop limit).</param>
/// <param name="PayloadSize">Payload size sent.</param>
public sealed record PingReplyInfo(int Sequence, DateTimeOffset Timestamp, IPAddress Target, IPAddress? From, IPStatus Status, long? RoundTripMs, int? Ttl, int PayloadSize)
{
    /// <summary>True for an echo reply.</summary>
    public bool Succeeded => Status == IPStatus.Success;

    /// <summary>Status text.</summary>
    public string StatusText => PingTool.Describe(Status);

    /// <summary>Round-trip text ("&lt;1 ms", "23 ms", "—").</summary>
    public string RoundTripText => RoundTripMs is { } ms ? (ms == 0 ? "<1 ms" : string.Create(CultureInfo.CurrentCulture, $"{ms} ms")) : "—";

    /// <summary>TTL text.</summary>
    public string TtlText => Ttl?.ToString(CultureInfo.CurrentCulture) ?? "—";

    /// <summary>Responder text.</summary>
    public string FromText => From?.ToString() ?? "—";
}

/// <summary>Statistics of a ping run.</summary>
/// <param name="Sent">Requests sent.</param>
/// <param name="Received">Echo replies received.</param>
/// <param name="LossPercent">Lost requests in percent.</param>
/// <param name="MinimumMs">Minimum round trip.</param>
/// <param name="AverageMs">Average round trip.</param>
/// <param name="MaximumMs">Maximum round trip.</param>
/// <param name="JitterMs">Mean absolute difference between consecutive round trips (needs two replies).</param>
public sealed record PingSummary(int Sent, int Received, double LossPercent, long? MinimumMs, double? AverageMs, long? MaximumMs, double? JitterMs)
{
    /// <summary>No requests.</summary>
    public static PingSummary Empty { get; } = new(0, 0, 0, null, null, null, null);
}

/// <summary>ICMP echo (ping) using the Windows ICMP API through <see cref="Ping"/>; works without elevation.</summary>
public static class PingTool
{
    private const string PayloadPattern = "abcdefghijklmnopqrstuvwabcdefghi";

    /// <summary>Sends echo requests to <paramref name="target"/> and yields each result as it arrives.</summary>
    /// <exception cref="DiagnosticsException">The request could not be sent at all (for example no route to the address family).</exception>
    public static async IAsyncEnumerable<PingReplyInfo> RunAsync(IPAddress target, PingSettings settings, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        using var ping = new Ping();
        byte[] payload = CreatePayload(settings.PayloadSize);
        var options = new PingOptions(settings.Ttl, settings.DontFragment);
        for (int sequence = 1; settings.Count == 0 || sequence <= settings.Count; sequence++)
        {
            long started = Stopwatch.GetTimestamp();
            var timestamp = DateTimeOffset.UtcNow;
            PingReplyInfo info;
            try
            {
                var reply = await ping.SendPingAsync(target, settings.Timeout, payload, options, cancellationToken).ConfigureAwait(false);
                bool ok = reply.Status == IPStatus.Success;
                info = new PingReplyInfo(
                    sequence,
                    timestamp,
                    target,
                    IsUnspecified(reply.Address) ? null : reply.Address,
                    reply.Status,
                    ok ? reply.RoundtripTime : null,
                    ok ? reply.Options?.Ttl : null,
                    settings.PayloadSize);
            }
            catch (PingException ex)
            {
                throw new DiagnosticsException("The echo request could not be sent: " + ErrorText.Describe(ex), ex);
            }

            yield return info;
            if (settings.Count != 0 && sequence >= settings.Count)
            {
                yield break;
            }

            var wait = settings.Interval - Stopwatch.GetElapsedTime(started);
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Computes loss, min/avg/max and jitter.</summary>
    public static PingSummary Summarize(IReadOnlyCollection<PingReplyInfo> replies)
    {
        ArgumentNullException.ThrowIfNull(replies);
        int sent = replies.Count;
        if (sent == 0)
        {
            return PingSummary.Empty;
        }

        var rtts = replies.OrderBy(r => r.Sequence).Where(r => r.Succeeded && r.RoundTripMs.HasValue).Select(r => r.RoundTripMs!.Value).ToList();
        double loss = (sent - rtts.Count) * 100.0 / sent;
        if (rtts.Count == 0)
        {
            return new PingSummary(sent, 0, loss, null, null, null, null);
        }

        double? jitter = null;
        if (rtts.Count > 1)
        {
            double sum = 0;
            for (int i = 1; i < rtts.Count; i++)
            {
                sum += Math.Abs(rtts[i] - rtts[i - 1]);
            }

            jitter = sum / (rtts.Count - 1);
        }

        return new PingSummary(sent, rtts.Count, loss, rtts.Min(), rtts.Average(), rtts.Max(), jitter);
    }

    /// <summary>User-facing text for an ICMP status.</summary>
    public static string Describe(IPStatus status) => status switch
    {
        IPStatus.Success => "Reply",
        IPStatus.TimedOut => "Request timed out",
        IPStatus.DestinationHostUnreachable => "Destination host unreachable",
        IPStatus.DestinationNetworkUnreachable => "Destination network unreachable",
        IPStatus.DestinationPortUnreachable => "Destination port unreachable",
        IPStatus.DestinationProtocolUnreachable => "Destination protocol unreachable / prohibited",
        IPStatus.DestinationUnreachable => "Destination unreachable",
        IPStatus.TtlExpired => "TTL expired in transit",
        IPStatus.TimeExceeded => "Time exceeded",
        IPStatus.TtlReassemblyTimeExceeded => "Reassembly time exceeded",
        IPStatus.PacketTooBig => "Packet too big (fragmentation needed, DF set)",
        IPStatus.BadRoute => "Bad route",
        IPStatus.BadDestination => "Bad destination",
        IPStatus.NoResources => "Insufficient network resources",
        IPStatus.HardwareError => "Hardware error",
        IPStatus.ParameterProblem => "Parameter problem",
        IPStatus.SourceQuench => "Source quench",
        IPStatus.DestinationScopeMismatch => "Destination scope mismatch",
        IPStatus.BadHeader => "Bad header",
        IPStatus.UnrecognizedNextHeader => "Unrecognized next header",
        IPStatus.IcmpError => "ICMP error",
        _ => status.ToString(),
    };

    internal static bool IsUnspecified(IPAddress? address) =>
        address is null || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any);

    internal static byte[] CreatePayload(int size)
    {
        var payload = new byte[size];
        for (int i = 0; i < size; i++)
        {
            payload[i] = (byte)PayloadPattern[i % PayloadPattern.Length];
        }

        return payload;
    }
}
