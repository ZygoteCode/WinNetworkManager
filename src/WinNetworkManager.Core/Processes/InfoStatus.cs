namespace WinNetworkManager.Core.Processes;

/// <summary>Why a piece of information is or is not available. Shown to the user instead of empty values.</summary>
public enum InfoStatus : byte
{
    /// <summary>Not queried yet.</summary>
    Pending = 0,

    /// <summary>Value available.</summary>
    Available,

    /// <summary>Windows denied access (protected process, other user, insufficient privileges).</summary>
    AccessDenied,

    /// <summary>The process (or file) no longer exists.</summary>
    NotFound,

    /// <summary>The concept does not apply (e.g. no executable for the System process).</summary>
    NotApplicable,

    /// <summary>The query failed for another reason.</summary>
    Failed,
}

/// <summary>A value together with the status explaining its absence.</summary>
/// <typeparam name="T">Value type.</typeparam>
/// <param name="Value">The value when <see cref="Status"/> is <see cref="InfoStatus.Available"/>.</param>
/// <param name="Status">Availability.</param>
public readonly record struct Probe<T>(T? Value, InfoStatus Status)
{
    /// <summary>True when a value is present.</summary>
    public bool HasValue => Status == InfoStatus.Available;

    /// <summary>Creates an available value.</summary>
    public static Probe<T> Of(T value) => new(value, InfoStatus.Available);

    /// <summary>Creates a missing value.</summary>
    public static Probe<T> Missing(InfoStatus status) => new(default, status);

    /// <summary>Short user-facing explanation for a missing value.</summary>
    public static string Explain(InfoStatus status) => status switch
    {
        InfoStatus.Pending => "Loading…",
        InfoStatus.AccessDenied => "Access denied",
        InfoStatus.NotFound => "Process exited",
        InfoStatus.NotApplicable => "Not applicable",
        InfoStatus.Failed => "Unavailable",
        _ => string.Empty,
    };
}
