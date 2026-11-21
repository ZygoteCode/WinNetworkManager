using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using WinNetworkManager.Core.Errors;

namespace WinNetworkManager.Diagnostics;

/// <summary>Outcome of a TCP connection test.</summary>
public enum TcpProbeOutcome
{
    /// <summary>The three-way handshake completed.</summary>
    Connected,

    /// <summary>The host answered with a reset: reachable, but nothing accepts connections on that port.</summary>
    Refused,

    /// <summary>No answer before the timeout (filtered by a firewall, host down, or packet loss).</summary>
    TimedOut,

    /// <summary>The local stack or a router reported the network or host as unreachable.</summary>
    Unreachable,

    /// <summary>Other failure.</summary>
    Failed,
}

/// <summary>Result of one TCP connection test.</summary>
/// <param name="Remote">Endpoint tested.</param>
/// <param name="Outcome">Outcome.</param>
/// <param name="ConnectMs">Handshake time (only for successful connections; it includes one round trip).</param>
/// <param name="Local">Local endpoint used, when connected.</param>
/// <param name="Error">Socket error, when failed.</param>
/// <param name="Message">User-facing explanation.</param>
public sealed record TcpProbeResult(IPEndPoint Remote, TcpProbeOutcome Outcome, double? ConnectMs, IPEndPoint? Local, SocketError? Error, string Message)
{
    /// <summary>Time text.</summary>
    public string ConnectText => ConnectMs is { } ms ? ms.ToString(ms < 10 ? "0.0" : "0", CultureInfo.CurrentCulture) + " ms" : "—";

    /// <summary>Local endpoint text.</summary>
    public string LocalText => Local?.ToString() ?? "—";
}

/// <summary>
/// Tests whether a TCP port accepts connections with a normal connect followed by an immediate graceful close
/// (no data is sent; no half-open or stealth techniques are used).
/// </summary>
public static class TcpConnectTool
{
    /// <summary>Connects to <paramref name="address"/>:<paramref name="port"/>.</summary>
    public static async Task<TcpProbeResult> ProbeAsync(IPAddress address, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        var remote = new IPEndPoint(address, port);
        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        long started = Stopwatch.GetTimestamp();
        try
        {
            await socket.ConnectAsync(remote, timeoutSource.Token).ConfigureAwait(false);
            double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var local = socket.LocalEndPoint as IPEndPoint;
            try
            {
                socket.Shutdown(SocketShutdown.Both);
            }
            catch (SocketException)
            {
                // The peer may already have closed; the test result is unaffected.
            }

            return new TcpProbeResult(remote, TcpProbeOutcome.Connected, elapsed, local, null,
                string.Create(CultureInfo.CurrentCulture, $"Connected: the port accepts TCP connections (handshake {elapsed:0.0} ms)."));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new TcpProbeResult(remote, TcpProbeOutcome.TimedOut, null, null, SocketError.TimedOut,
                string.Create(CultureInfo.CurrentCulture, $"No answer within {timeout.TotalMilliseconds:0} ms: a firewall may drop the connection, or the host is down."));
        }
        catch (SocketException ex)
        {
            var outcome = Classify(ex.SocketErrorCode);
            return new TcpProbeResult(remote, outcome, null, null, ex.SocketErrorCode, Describe(outcome, ex));
        }
    }

    /// <summary>Maps a socket error to an outcome.</summary>
    internal static TcpProbeOutcome Classify(SocketError error) => error switch
    {
        SocketError.ConnectionRefused => TcpProbeOutcome.Refused,
        SocketError.TimedOut => TcpProbeOutcome.TimedOut,
        SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.NetworkDown or SocketError.HostDown or SocketError.AddressNotAvailable => TcpProbeOutcome.Unreachable,
        _ => TcpProbeOutcome.Failed,
    };

    private static string Describe(TcpProbeOutcome outcome, SocketException ex) => outcome switch
    {
        TcpProbeOutcome.Refused => "Refused: the host is reachable but nothing accepts connections on this port (or a firewall answered with a reset).",
        TcpProbeOutcome.TimedOut => "No answer: a firewall may drop the connection, or the host is down.",
        TcpProbeOutcome.Unreachable => "Unreachable: " + ErrorText.Socket(ex.SocketErrorCode, ex.ErrorCode),
        _ => "Failed: " + ErrorText.Socket(ex.SocketErrorCode, ex.ErrorCode),
    };
}
