using WinNetworkManager.Core.Errors;

namespace WinNetworkManager.Diagnostics;

/// <summary>A diagnostic operation failed for a reason whose message can be shown to the user as is.</summary>
public sealed class DiagnosticsException : Exception, IUserFacingMessage
{
    /// <summary>Creates the exception.</summary>
    public DiagnosticsException()
    {
    }

    /// <summary>Creates the exception with a user-facing message.</summary>
    public DiagnosticsException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a user-facing message and the underlying cause.</summary>
    public DiagnosticsException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
