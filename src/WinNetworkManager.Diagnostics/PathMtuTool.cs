using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using WinNetworkManager.Core.Errors;

namespace WinNetworkManager.Diagnostics;

/// <summary>Result of a path MTU measurement.</summary>
/// <param name="Target">Destination.</param>
/// <param name="Mtu">Largest IPv4 packet size that crossed the path unfragmented, or null when it could not be measured.</param>
/// <param name="AtLeast">True when even the largest size tried got through (the real MTU may be larger).</param>
/// <param name="Probes">Echo requests sent.</param>
/// <param name="Message">User-facing explanation.</param>
public sealed record PathMtuResult(IPAddress Target, int? Mtu, bool AtLeast, int Probes, string Message);

/// <summary>
/// Measures the IPv4 path MTU by sending echo requests with the "don't fragment" flag and a binary search on the size.
/// IPv6 is not supported: Windows does not expose a "don't fragment" option for ICMPv6 echo, so results would be
/// meaningless. Hosts or networks that drop ICMP cannot be measured this way.
/// </summary>
public static class PathMtuTool
{
    /// <summary>IPv4 header (20) + ICMP header (8).</summary>
    public const int HeaderBytes = 28;

    /// <summary>Largest packet size tried (jumbo frames).</summary>
    public const int MaximumMtu = 9000;

    /// <summary>Runs the measurement.</summary>
    public static async Task<PathMtuResult> DiscoverAsync(IPAddress target, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.AddressFamily != AddressFamily.InterNetwork)
        {
            return new PathMtuResult(target, null, false, 0,
                "Path MTU measurement needs an IPv4 destination: Windows offers no \"don't fragment\" option for ICMPv6 echo requests.");
        }

        using var ping = new Ping();
        var options = new PingOptions(64, dontFragment: true);
        int probes = 0;

        async Task<IPStatus> ProbeAsync(int payloadSize)
        {
            probes++;
            try
            {
                var reply = await ping.SendPingAsync(target, timeout, new byte[payloadSize], options, cancellationToken).ConfigureAwait(false);
                return reply.Status;
            }
            catch (PingException ex)
            {
                throw new DiagnosticsException("The probe could not be sent: " + ErrorText.Describe(ex), ex);
            }
        }

        var first = await ProbeAsync(32).ConfigureAwait(false);
        if (first != IPStatus.Success)
        {
            return new PathMtuResult(target, null, false, probes,
                $"The destination did not answer a small echo request ({PingTool.Describe(first)}). It may block ICMP, so the path MTU cannot be measured with echo requests.");
        }

        int low = 32;                       // known to pass
        int high = MaximumMtu - HeaderBytes; // candidate upper bound
        var common = await ProbeAsync(1500 - HeaderBytes).ConfigureAwait(false);
        if (common == IPStatus.Success)
        {
            low = 1500 - HeaderBytes;
            if (await ProbeAsync(high).ConfigureAwait(false) == IPStatus.Success)
            {
                return new PathMtuResult(target, MaximumMtu, true, probes,
                    string.Create(CultureInfo.CurrentCulture, $"Packets of {MaximumMtu} bytes reach the destination unfragmented (the largest size tested)."));
            }
        }
        else if (common is not (IPStatus.PacketTooBig or IPStatus.TimedOut))
        {
            return new PathMtuResult(target, null, false, probes, "Unexpected answer: " + PingTool.Describe(common) + ".");
        }

        if (common != IPStatus.Success)
        {
            high = 1500 - HeaderBytes - 1;
        }

        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            var status = await ProbeAsync(mid).ConfigureAwait(false);
            if (status == IPStatus.TimedOut)
            {
                // A timeout is either a black-holed large packet or ordinary loss: ask once more before deciding.
                status = await ProbeAsync(mid).ConfigureAwait(false);
            }

            if (status == IPStatus.Success)
            {
                low = mid;
            }
            else if (status is IPStatus.PacketTooBig or IPStatus.TimedOut)
            {
                high = mid - 1;
            }
            else
            {
                return new PathMtuResult(target, null, false, probes, "Unexpected answer: " + PingTool.Describe(status) + ".");
            }
        }

        int mtu = low + HeaderBytes;
        return new PathMtuResult(target, mtu, false, probes,
            string.Create(CultureInfo.CurrentCulture, $"The largest packet that reached the destination unfragmented is {mtu} bytes ({low} bytes of data + {HeaderBytes} bytes of IPv4/ICMP headers)."));
    }
}
