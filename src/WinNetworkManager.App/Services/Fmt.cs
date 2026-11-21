using System.Globalization;
using WinNetworkManager.Core.Networking;
using WinNetworkManager.Core.Units;

namespace WinNetworkManager.App.Services;

/// <summary>Display formatting shared by all view models; follows the unit preference.</summary>
public static class Fmt
{
    /// <summary>Current unit system (set from settings on the UI thread).</summary>
    public static DataUnitSystem Units { get; set; } = DataUnitSystem.DecimalBytes;

    /// <summary>Text shown where a value cannot be measured.</summary>
    public const string NotAvailable = "—";

    /// <summary>Formats a rate.</summary>
    public static string Rate(double bytesPerSecond) => DataFormatter.FormatRate(bytesPerSecond, Units);

    /// <summary>Formats a rate, or "—" when unavailable.</summary>
    public static string Rate(double bytesPerSecond, bool available) => available ? Rate(bytesPerSecond) : NotAvailable;

    /// <summary>Formats a quantity.</summary>
    public static string Bytes(long bytes) => DataFormatter.FormatBytes(bytes, Units);

    /// <summary>Formats a quantity, or "—" when unavailable.</summary>
    public static string Bytes(long bytes, bool available) => available ? Bytes(bytes) : NotAvailable;

    /// <summary>Exact byte count for tooltips.</summary>
    public static string Exact(long bytes) => DataFormatter.FormatExactBytes(bytes);

    /// <summary>Compact duration: "45 s", "12 min", "3 h 05 min", "2 d 4 h".</summary>
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            return NotAvailable;
        }

        return span.TotalSeconds < 60 ? $"{span.TotalSeconds:0} s"
            : span.TotalMinutes < 60 ? $"{(int)span.TotalMinutes} min {span.Seconds:00} s"
            : span.TotalHours < 24 ? $"{(int)span.TotalHours} h {span.Minutes:00} min"
            : $"{(int)span.TotalDays} d {span.Hours} h";
    }

    /// <summary>Local time with seconds.</summary>
    public static string Time(DateTimeOffset? value) => value is { } v ? v.ToLocalTime().ToString("T", CultureInfo.CurrentCulture) : NotAvailable;

    /// <summary>Local date and time.</summary>
    public static string DateTime(DateTimeOffset? value) => value is { } v ? v.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : NotAvailable;

    /// <summary>Integer with group separators.</summary>
    public static string Count(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>Endpoint text with brackets for IPv6.</summary>
    public static string Endpoint(IpEndpointValue endpoint) => endpoint.IsEmpty ? NotAvailable : endpoint.ToString();

    /// <summary>Address text, "*" for wildcard binds.</summary>
    public static string Address(IpAddressValue address) => address.IsEmpty ? NotAvailable : address.IsUnspecified ? "*" : address.ToString();

    /// <summary>Port text, "*" for 0.</summary>
    public static string Port(ushort port) => port == 0 ? "*" : port.ToString(CultureInfo.InvariantCulture);

    /// <summary>Link speed in bits per second ("1 Gbps").</summary>
    public static string LinkSpeed(long bitsPerSecond)
    {
        if (bitsPerSecond <= 0)
        {
            return NotAvailable;
        }

        string[] units = ["bps", "kbps", "Mbps", "Gbps", "Tbps"];
        double v = bitsPerSecond;
        int i = 0;
        while (v >= 1000 && i < units.Length - 1)
        {
            v /= 1000;
            i++;
        }

        return v.ToString(v < 10 ? "0.#" : "0", CultureInfo.CurrentCulture) + " " + units[i];
    }
}
