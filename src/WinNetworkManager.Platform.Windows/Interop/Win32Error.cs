using System.ComponentModel;
using System.Runtime.InteropServices;
using WinNetworkManager.Core.Errors;

namespace WinNetworkManager.Platform.Windows.Interop;

/// <summary>Win32 error codes used by this library.</summary>
internal static class Win32Error
{
    public const uint ERROR_SUCCESS = 0;
    public const uint ERROR_FILE_NOT_FOUND = 2;
    public const uint ERROR_ACCESS_DENIED = 5;
    public const uint ERROR_INVALID_HANDLE = 6;
    public const uint ERROR_NO_MORE_FILES = 18;
    public const uint ERROR_NOT_ENOUGH_MEMORY = 8;
    public const uint ERROR_BAD_LENGTH = 24;
    public const uint ERROR_NOT_SUPPORTED = 50;
    public const uint ERROR_INVALID_PARAMETER = 87;
    public const uint ERROR_INSUFFICIENT_BUFFER = 122;
    public const uint ERROR_ALREADY_EXISTS = 183;
    public const uint ERROR_MORE_DATA = 234;
    public const uint ERROR_NO_MORE_ITEMS = 259;
    public const uint ERROR_NOT_FOUND = 1168;
    public const uint ERROR_CANCELLED = 1223;
    public const uint ERROR_NO_DATA = 232;
    public const uint ERROR_WMI_INSTANCE_NOT_FOUND = 4201;
    public const uint ERROR_CTX_CLOSE_PENDING = 7007;
}

/// <summary>A Win32 API failure with the API name and the error code.</summary>
public sealed class NativeCallException : Win32Exception, IUserFacingMessage
{
    /// <summary>Creates an exception for <paramref name="api"/> failing with <paramref name="error"/>.</summary>
    public NativeCallException(string api, uint error)
        : base((int)error, $"{api} failed: {ErrorText.Win32((int)error)}")
    {
        Api = api;
        Error = error;
    }

    /// <summary>Creates an exception with a message.</summary>
    public NativeCallException()
        : base()
    {
        Api = string.Empty;
    }

    /// <summary>Creates an exception with a message.</summary>
    public NativeCallException(string message)
        : base(message)
    {
        Api = string.Empty;
    }

    /// <summary>Creates an exception with a message and inner exception.</summary>
    public NativeCallException(string message, Exception innerException)
        : base(message, innerException)
    {
        Api = string.Empty;
    }

    /// <summary>Name of the failing API.</summary>
    public string Api { get; }

    /// <summary>Win32 error code.</summary>
    public uint Error { get; }

    /// <summary>True when the failure is an access check failure.</summary>
    public bool IsAccessDenied => Error == Win32Error.ERROR_ACCESS_DENIED;

    /// <summary>Throws for the calling thread's last Win32 error.</summary>
    public static NativeCallException FromLastError(string api) => new(api, (uint)Marshal.GetLastPInvokeError());
}
