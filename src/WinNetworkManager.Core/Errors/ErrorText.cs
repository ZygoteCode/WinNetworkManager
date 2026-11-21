using System.ComponentModel;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;

namespace WinNetworkManager.Core.Errors;

/// <summary>Marks exceptions whose <see cref="Exception.Message"/> is already an English, user-facing sentence.</summary>
public interface IUserFacingMessage
{
}

/// <summary>
/// English, user-facing descriptions of failures. Windows formats the messages of <see cref="SocketException"/>,
/// <see cref="Win32Exception"/>, COM and WMI errors in the display language of the system; showing them would mix
/// languages in the interface. This class maps the underlying codes to English text instead (with the numeric code, so
/// unknown errors remain searchable).
/// </summary>
public static class ErrorText
{
    /// <summary>Describes <paramref name="exception"/> in English.</summary>
    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            IUserFacingMessage => exception.Message,
            SocketException socket => Socket(socket.SocketErrorCode, socket.ErrorCode),
            PingException { InnerException: { } inner } => Describe(inner),
            AuthenticationException { InnerException: Win32Exception inner } => "The TLS handshake failed: " + Lower(Win32(inner.NativeErrorCode)),
            Win32Exception win32 => Win32(win32.NativeErrorCode),
            COMException com => Com(com.HResult),
            IOException io when IsWin32HResult(io.HResult) && !HasEnglishRuntimeMessage(io.HResult & 0xFFFF) => Win32(io.HResult & 0xFFFF),
            AggregateException { InnerExceptions.Count: 1 } aggregate => Describe(aggregate.InnerExceptions[0]),
            _ => exception.Message,
        };
    }

    /// <summary>Describes a socket error.</summary>
    public static string Socket(SocketError error, int code)
    {
        string text = error switch
        {
            SocketError.AccessDenied => "Access denied (blocked by a firewall or security policy, or the port is reserved)",
            SocketError.AddressAlreadyInUse => "The address and port are already in use",
            SocketError.AddressNotAvailable => "The address is not valid on this computer",
            SocketError.AddressFamilyNotSupported => "This address family is not supported (for example IPv6 is turned off)",
            SocketError.ConnectionAborted => "The connection was aborted on this computer",
            SocketError.ConnectionRefused => "The connection was refused",
            SocketError.ConnectionReset => "The connection was reset by the remote host",
            SocketError.HostDown => "The remote host is down",
            SocketError.HostNotFound => "The host name could not be found",
            SocketError.HostUnreachable => "There is no route to the host",
            SocketError.NetworkDown => "The network is down",
            SocketError.NetworkUnreachable => "The network is unreachable",
            SocketError.NetworkReset => "The connection was dropped by a network reset",
            SocketError.NoBufferSpaceAvailable => "No buffer space is available (too many connections or low memory)",
            SocketError.NoData => "The name exists but has no address of the requested type",
            SocketError.NoRecovery => "A non-recoverable name resolution error occurred",
            SocketError.NotConnected => "The socket is not connected",
            SocketError.OperationAborted => "The operation was cancelled",
            SocketError.Shutdown => "The connection was already shut down",
            SocketError.TimedOut => "The operation timed out",
            SocketError.TooManyOpenSockets => "Too many sockets are open",
            SocketError.TryAgain => "Temporary name resolution failure; try again",
            SocketError.MessageSize => "The message is too large",
            SocketError.ProtocolNotSupported or SocketError.SocketNotSupported or SocketError.OperationNotSupported or SocketError.ProtocolFamilyNotSupported => "Not supported by the network stack",
            SocketError.InvalidArgument => "Invalid argument",
            SocketError.WouldBlock or SocketError.IOPending or SocketError.InProgress or SocketError.AlreadyInProgress => "The operation is still in progress",
            SocketError.SystemNotReady => "The network subsystem is not ready",
            SocketError.VersionNotSupported or SocketError.NotInitialized => "Windows Sockets is not available",
            SocketError.Disconnecting => "The connection is closing",
            _ => "Network error",
        };
        return string.Create(CultureInfo.InvariantCulture, $"{text} (socket error {code}).");
    }

    /// <summary>Describes a Win32 error code (also SSPI/TLS status codes reported through <see cref="Win32Exception"/>).</summary>
    public static string Win32(int code)
    {
        string? text = code switch
        {
            2 => "The file or object was not found",
            3 => "The path was not found",
            5 => "Access denied",
            6 => "Invalid handle",
            8 or 14 => "Not enough memory",
            21 => "The device is not ready",
            32 => "The file is in use by another process",
            50 => "Not supported",
            53 => "The network path was not found",
            64 => "The network name is no longer available",
            67 => "The network name cannot be found",
            87 => "Invalid parameter",
            109 => "The other end of the pipe was closed",
            112 => "There is not enough space on the disk",
            121 => "The operation timed out",
            122 => "The buffer is too small",
            183 => "It already exists",
            206 => "The file name or path is too long",
            231 => "All pipe instances are busy",
            232 => "The pipe is being closed",
            233 => "No process is on the other end of the pipe",
            234 => "More data is available",
            259 => "No more data is available",
            740 => "The operation requires elevation (run as administrator)",
            995 => "The operation was cancelled",
            1053 => "The service did not respond in time",
            1060 => "The service does not exist",
            1062 => "The service has not been started",
            1168 => "Element not found",
            1223 => "The operation was cancelled by the user",
            1225 => "The remote computer refused the connection",
            1231 => "The network location cannot be reached",
            1232 => "The host is unreachable",
            1236 => "The connection was aborted",
            1314 => "A required privilege is not held",
            1450 => "Insufficient system resources",
            4201 => "The instance was not found",
            11001 => "The reply buffer is too small",
            11002 => "Destination network unreachable",
            11003 => "Destination host unreachable",
            11004 => "Destination protocol unreachable",
            11005 => "Destination port unreachable",
            11006 => "Insufficient network resources",
            11009 => "Packet too big",
            11010 => "Request timed out",
            11013 => "TTL expired in transit",
            11050 => "General failure: the request could not be sent (no usable route or interface)",
            unchecked((int)0x80090302) => "The client and the server support no common TLS version or cipher suite",
            unchecked((int)0x80090304) => "Internal TLS error",
            unchecked((int)0x80090308) => "The TLS handshake data is invalid",
            unchecked((int)0x8009030F) => "A TLS message was altered",
            unchecked((int)0x80090322) => "The certificate does not match the host name",
            unchecked((int)0x80090325) => "The certificate chain ends in an untrusted root",
            unchecked((int)0x80090326) => "The server sent an unexpected or malformed TLS message",
            unchecked((int)0x80090327) => "The certificate could not be processed",
            unchecked((int)0x80090328) => "The certificate has expired",
            unchecked((int)0x80090330) => "TLS decryption failed",
            unchecked((int)0x80090331) => "The client and the server support no common algorithm",
            unchecked((int)0x80092010) => "The certificate has been revoked",
            unchecked((int)0x80092012) => "The revocation status could not be checked",
            unchecked((int)0x80092013) => "The revocation server could not be reached",
            _ => null,
        };
        if (text is null && IsWin32HResult(code))
        {
            return Win32(code & 0xFFFF);
        }

        return code is < 0 or > 0xFFFF
            ? string.Create(CultureInfo.InvariantCulture, $"{text ?? "Windows error"} (0x{code:X8}).")
            : string.Create(CultureInfo.InvariantCulture, $"{text ?? "Windows error"} (error {code}).");
    }

    /// <summary>Describes a COM or WMI HRESULT.</summary>
    public static string Com(int hresult)
    {
        if (IsWin32HResult(hresult))
        {
            return Win32(hresult & 0xFFFF);
        }

        string text = hresult switch
        {
            unchecked((int)0x800706BA) => "The RPC server is unavailable",
            unchecked((int)0x80041001) => "The WMI call failed",
            unchecked((int)0x80041002) => "The WMI object was not found",
            unchecked((int)0x80041003) => "WMI access denied",
            unchecked((int)0x8004100E) => "The WMI namespace is not available",
            unchecked((int)0x80041010) => "The WMI class is not available on this version of Windows",
            unchecked((int)0x80041013) => "The WMI provider could not be loaded",
            unchecked((int)0x80041032) => "The WMI call was cancelled",
            unchecked((int)0x80043001) => "The WMI call timed out",
            unchecked((int)0x80004005) => "Unspecified failure",
            unchecked((int)0x80004001) => "Not implemented",
            unchecked((int)0x80040154) => "The component is not registered",
            _ => "COM error",
        };
        return string.Create(CultureInfo.InvariantCulture, $"{text} (0x{hresult:X8}).");
    }

    /// <summary>True for HRESULTs that wrap a Win32 error (facility 7).</summary>
    public static bool IsWin32HResult(int hresult) => (hresult & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000);

    /// <summary>Win32 codes for which .NET throws its own (English) message rather than the system text.</summary>
    private static bool HasEnglishRuntimeMessage(int code) => code is 2 or 3 or 5 or 15 or 32 or 80 or 183 or 206 or 995;

    private static string Lower(string text) => text.Length > 0 ? char.ToLowerInvariant(text[0]) + text[1..] : text;
}
