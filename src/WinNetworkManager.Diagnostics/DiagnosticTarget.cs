using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using WinNetworkManager.Core.Errors;

namespace WinNetworkManager.Diagnostics;

/// <summary>Which address family to use when a name resolves to both.</summary>
public enum AddressPreference
{
    /// <summary>Use the order returned by the system resolver.</summary>
    System,

    /// <summary>IPv4 only.</summary>
    IPv4,

    /// <summary>IPv6 only.</summary>
    IPv6,
}

/// <summary>A destination named by the user, resolved to addresses.</summary>
/// <param name="Input">What the user typed.</param>
/// <param name="Host">Normalized host (ASCII name or address literal).</param>
/// <param name="Addresses">Addresses, in resolver order.</param>
/// <param name="ResolveMs">Time spent resolving the name, or null for address literals.</param>
/// <param name="IsLiteral">True when the input was an IP address.</param>
public sealed record ResolvedTarget(string Input, string Host, IReadOnlyList<IPAddress> Addresses, double? ResolveMs, bool IsLiteral);

/// <summary>
/// Validates and resolves user-entered destinations. Every tool works on exactly one destination chosen by the user:
/// address ranges, subnets and wildcards are rejected on purpose (no network sweeps).
/// </summary>
public static class DiagnosticTarget
{
    private static readonly SearchValues<char> Forbidden = SearchValues.Create(" \t\r\n/,;*");

    /// <summary>Normalizes <paramref name="input"/> (accepts host names, IPv4/IPv6 literals, [IPv6] and URLs).</summary>
    public static bool TryNormalize(string? input, out string host, out string? error)
    {
        host = string.Empty;
        error = null;
        string text = (input ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            error = "Enter a host name or an IP address.";
            return false;
        }

        if (text.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            {
                error = "The address is not a valid URL.";
                return false;
            }

            text = uri.HostNameType == UriHostNameType.IPv6 ? uri.DnsSafeHost : uri.IdnHost;
        }

        if (text.Length > 2 && text[0] == '[' && text[^1] == ']')
        {
            text = text[1..^1];
        }

        if (text.AsSpan().IndexOfAny(Forbidden) >= 0)
        {
            error = "Enter a single host name or address. Ranges, subnets and lists are not accepted: the tools only test destinations you name.";
            return false;
        }

        if (LooksLikeAddress(text))
        {
            if (IPAddress.TryParse(text, out var address))
            {
                host = address.ToString();
                return true;
            }

            error = "The IP address is not valid.";
            return false;
        }

        try
        {
            host = DnsWire.ToAsciiName(text);
        }
        catch (DiagnosticsException ex)
        {
            error = ex.Message;
            return false;
        }

        if (host.Length == 0)
        {
            error = "Enter a host name or an IP address.";
            return false;
        }

        string lastLabel = host[(host.LastIndexOf('.') + 1)..];
        if (lastLabel.All(char.IsAsciiDigit))
        {
            // A top-level label is never numeric; the system resolver would read "123" or "10.1" as a legacy IPv4 form.
            error = "This looks like an incomplete IP address. Enter all four parts (for example 192.168.1.1).";
            host = string.Empty;
            return false;
        }

        return true;
    }

    /// <summary>Resolves <paramref name="input"/> with the system resolver (hosts file, DNS cache, DNS, LLMNR/mDNS).</summary>
    /// <exception cref="DiagnosticsException">The input is invalid or the name does not resolve.</exception>
    public static async Task<ResolvedTarget> ResolveAsync(string input, AddressPreference preference, CancellationToken cancellationToken)
    {
        if (!TryNormalize(input, out string host, out string? error))
        {
            throw new DiagnosticsException(error ?? "Invalid destination.");
        }

        if (IPAddress.TryParse(host, out var literal))
        {
            if ((preference == AddressPreference.IPv4 && literal.AddressFamily != AddressFamily.InterNetwork) ||
                (preference == AddressPreference.IPv6 && literal.AddressFamily != AddressFamily.InterNetworkV6))
            {
                throw new DiagnosticsException($"{host} is not an {(preference == AddressPreference.IPv4 ? "IPv4" : "IPv6")} address.");
            }

            return new ResolvedTarget(input, host, [literal], null, true);
        }

        var family = preference switch
        {
            AddressPreference.IPv4 => AddressFamily.InterNetwork,
            AddressPreference.IPv6 => AddressFamily.InterNetworkV6,
            _ => AddressFamily.Unspecified,
        };
        long start = Stopwatch.GetTimestamp();
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, family, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            throw new DiagnosticsException(DescribeResolverError(host, ex), ex);
        }

        double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        if (addresses.Length == 0)
        {
            throw new DiagnosticsException(family == AddressFamily.Unspecified
                ? $"{host} resolved to no address."
                : $"{host} has no {(family == AddressFamily.InterNetwork ? "IPv4" : "IPv6")} address.");
        }

        return new ResolvedTarget(input, host, addresses, elapsed, false);
    }

    /// <summary>User-facing text for a resolver failure.</summary>
    public static string DescribeResolverError(string host, SocketException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.SocketErrorCode switch
        {
            SocketError.HostNotFound => $"{host} could not be resolved: the name does not exist (or no DNS server answered).",
            SocketError.TryAgain => $"{host} could not be resolved right now (temporary DNS failure). Try again.",
            SocketError.NoData => $"{host} exists but has no address records.",
            SocketError.NoRecovery => $"{host} could not be resolved (non-recoverable DNS error).",
            _ => $"{host} could not be resolved: {ErrorText.Socket(exception.SocketErrorCode, exception.ErrorCode)}",
        };
    }

    private static bool LooksLikeAddress(string text)
    {
        if (text.Contains(':', StringComparison.Ordinal))
        {
            return true; // IPv6 (host names never contain ':')
        }

        // Dotted quad: only digits and dots with exactly three dots. "10" or "1.2" are treated as names, not as
        // the legacy short IPv4 forms that would silently turn "123" into 0.0.0.123.
        int dots = 0;
        foreach (char c in text)
        {
            if (c == '.')
            {
                dots++;
            }
            else if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return dots == 3;
    }
}
