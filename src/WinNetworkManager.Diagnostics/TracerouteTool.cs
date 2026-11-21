using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using WinNetworkManager.Core.Errors;

namespace WinNetworkManager.Diagnostics;

/// <summary>Traceroute parameters.</summary>
public sealed record TracerouteSettings
{
    /// <summary>Maximum number of hops (1–64).</summary>
    public int MaxHops { get; init; } = 30;

    /// <summary>Probes per hop (1–5).</summary>
    public int ProbesPerHop { get; init; } = 3;

    /// <summary>Time to wait for each probe.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Reverse-resolve hop addresses (one PTR query per hop).</summary>
    public bool ResolveNames { get; init; }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxHops, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxHops, 64);
        ArgumentOutOfRangeException.ThrowIfLessThan(ProbesPerHop, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ProbesPerHop, 5);
        ArgumentOutOfRangeException.ThrowIfLessThan(Timeout, TimeSpan.FromMilliseconds(1));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Timeout, TimeSpan.FromSeconds(30));
    }
}

/// <summary>One hop of a traceroute.</summary>
/// <param name="Hop">Hop number (TTL used).</param>
/// <param name="Address">Router (or destination) that answered, or null when every probe timed out.</param>
/// <param name="RoundTripsMs">
/// Round trip of each probe in milliseconds, measured by this application around the request (Windows does not report
/// round-trip times for "TTL expired" answers), or null for probes that got no answer.
/// </param>
/// <param name="Status">Status of the answering probe (TtlExpired for intermediate routers, Success at the destination).</param>
/// <param name="HostName">Reverse DNS name, when requested and available.</param>
/// <param name="IsDestination">True when the destination answered.</param>
public sealed record TraceHop(int Hop, IPAddress? Address, IReadOnlyList<double?> RoundTripsMs, IPStatus Status, string? HostName, bool IsDestination)
{
    /// <summary>Responder text ("*" when no answer).</summary>
    public string AddressText => Address?.ToString() ?? "*";

    /// <summary>Round trips ("12.4 ms  11.9 ms  *").</summary>
    public string RoundTripsText => string.Join("   ", RoundTripsMs.Select(r => r is { } v ? v.ToString(v < 10 ? "0.0" : "0", CultureInfo.CurrentCulture) + " ms" : "*"));

    /// <summary>Status text.</summary>
    public string StatusText => Address is null ? "No answer" : IsDestination ? "Destination reached" : PingTool.Describe(Status);
}

/// <summary>
/// ICMP traceroute: echo requests with increasing TTL. Routers that drop ICMP or rate-limit "time exceeded"
/// messages appear as "*" even when traffic passes through them.
/// </summary>
public static class TracerouteTool
{
    /// <summary>Runs a traceroute to <paramref name="target"/>, yielding hops as they complete.</summary>
    public static async IAsyncEnumerable<TraceHop> RunAsync(IPAddress target, TracerouteSettings settings, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        using var ping = new Ping();
        byte[] payload = PingTool.CreatePayload(32);
        for (int ttl = 1; ttl <= settings.MaxHops; ttl++)
        {
            var options = new PingOptions(ttl, dontFragment: true);
            var rtts = new List<double?>(settings.ProbesPerHop);
            IPAddress? hopAddress = null;
            IPStatus status = IPStatus.TimedOut;
            for (int probe = 0; probe < settings.ProbesPerHop; probe++)
            {
                long started = Stopwatch.GetTimestamp();
                PingReply reply;
                try
                {
                    reply = await ping.SendPingAsync(target, settings.Timeout, payload, options, cancellationToken).ConfigureAwait(false);
                }
                catch (PingException ex)
                {
                    throw new DiagnosticsException("The probe could not be sent: " + ErrorText.Describe(ex), ex);
                }

                double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                bool answered = reply.Status != IPStatus.TimedOut && !PingTool.IsUnspecified(reply.Address);
                rtts.Add(answered ? elapsed : null);
                if (answered)
                {
                    hopAddress ??= reply.Address;
                    if (status != IPStatus.Success)
                    {
                        status = reply.Status;
                    }
                }
            }

            string? hostName = settings.ResolveNames && hopAddress is not null
                ? await ReverseLookupAsync(hopAddress, cancellationToken).ConfigureAwait(false)
                : null;
            bool destination = status == IPStatus.Success || (hopAddress is not null && hopAddress.Equals(target));
            yield return new TraceHop(ttl, hopAddress, rtts, status, hostName, destination);
            if (destination || IsFinal(status))
            {
                yield break;
            }
        }
    }

    /// <summary>Statuses that end a trace (the destination cannot be reached past this point).</summary>
    internal static bool IsFinal(IPStatus status) => status is IPStatus.DestinationHostUnreachable or IPStatus.DestinationNetworkUnreachable
        or IPStatus.DestinationProtocolUnreachable or IPStatus.DestinationPortUnreachable or IPStatus.DestinationUnreachable
        or IPStatus.BadDestination or IPStatus.BadRoute;

    /// <summary>Reverse lookup with a short timeout; null when there is no PTR record.</summary>
    internal static async Task<string?> ReverseLookupAsync(IPAddress address, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            var entry = await Dns.GetHostEntryAsync(address.ToString(), timeout.Token).ConfigureAwait(false);
            return string.IsNullOrEmpty(entry.HostName) || entry.HostName == address.ToString() ? null : entry.HostName;
        }
        catch (SocketException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}
