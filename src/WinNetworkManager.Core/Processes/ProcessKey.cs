using System.Globalization;

namespace WinNetworkManager.Core.Processes;

/// <summary>
/// Identity of a process instance. Windows recycles PIDs, so a PID alone is not an identity; the creation time
/// (FILETIME, 100 ns units, UTC) disambiguates instances. <see cref="CreateTimeTicks"/> is 0 when the creation time
/// could not be read (access denied); such keys are resolved by PID only and flagged as partial.
/// </summary>
public readonly record struct ProcessKey(int Pid, long CreateTimeTicks) : IComparable<ProcessKey>
{
    /// <summary>The idle pseudo-process (PID 0) — owner of TIME_WAIT rows.</summary>
    public static ProcessKey Idle => new(0, 0);

    /// <summary>True when the creation time is known.</summary>
    public bool HasCreateTime => CreateTimeTicks > 0;

    /// <summary>Creation time in UTC, if known.</summary>
    public DateTimeOffset? CreateTimeUtc =>
        CreateTimeTicks > 0 ? new DateTimeOffset(DateTime.FromFileTimeUtc(CreateTimeTicks), TimeSpan.Zero) : null;

    /// <summary>Stable textual form used for persistence and diagnostics: <c>pid@filetime</c>.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Pid}@{CreateTimeTicks}");

    /// <inheritdoc />
    public int CompareTo(ProcessKey other)
    {
        int c = Pid.CompareTo(other.Pid);
        return c != 0 ? c : CreateTimeTicks.CompareTo(other.CreateTimeTicks);
    }

    /// <summary>Ordering operator.</summary>
    public static bool operator <(ProcessKey left, ProcessKey right) => left.CompareTo(right) < 0;

    /// <summary>Ordering operator.</summary>
    public static bool operator >(ProcessKey left, ProcessKey right) => left.CompareTo(right) > 0;

    /// <summary>Ordering operator.</summary>
    public static bool operator <=(ProcessKey left, ProcessKey right) => left.CompareTo(right) <= 0;

    /// <summary>Ordering operator.</summary>
    public static bool operator >=(ProcessKey left, ProcessKey right) => left.CompareTo(right) >= 0;
}

/// <summary>Well-known PIDs.</summary>
public static class WellKnownPids
{
    /// <summary>System Idle Process.</summary>
    public const int Idle = 0;

    /// <summary>The System process (kernel-mode network clients such as SMB appear here).</summary>
    public const int System = 4;

    /// <summary>True for PIDs that are kernel pseudo-processes rather than user-mode programs.</summary>
    public static bool IsKernel(int pid) => pid is Idle or System;
}
