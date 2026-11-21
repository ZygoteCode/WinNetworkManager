using System.Globalization;

namespace WinNetworkManager.Core.Units;

/// <summary>How data quantities and rates are displayed.</summary>
public enum DataUnitSystem : byte
{
    /// <summary>SI decimal bytes: kB, MB, GB (powers of 1000). Rates in B/s.</summary>
    DecimalBytes = 0,

    /// <summary>IEC binary bytes: KiB, MiB, GiB (powers of 1024). Rates in B/s.</summary>
    BinaryBytes = 1,

    /// <summary>SI decimal bits for rates (kbit/s, Mbit/s); totals stay in decimal bytes.</summary>
    DecimalBits = 2,
}

/// <summary>Formats byte quantities and rates with explicit, unambiguous units.</summary>
public static class DataFormatter
{
    private static readonly string[] DecimalByteUnits = ["B", "kB", "MB", "GB", "TB", "PB", "EB"];
    private static readonly string[] BinaryByteUnits = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];
    private static readonly string[] DecimalBitUnits = ["bit", "kbit", "Mbit", "Gbit", "Tbit", "Pbit", "Ebit"];

    /// <summary>Text used when a value cannot be measured.</summary>
    public const string Unavailable = "—";

    /// <summary>Formats a byte quantity (totals). In <see cref="DataUnitSystem.DecimalBits"/> mode totals use decimal bytes.</summary>
    public static string FormatBytes(long bytes, DataUnitSystem system, IFormatProvider? provider = null)
    {
        if (bytes < 0)
        {
            return Unavailable;
        }

        return system == DataUnitSystem.BinaryBytes
            ? Scale(bytes, 1024, BinaryByteUnits, string.Empty, provider)
            : Scale(bytes, 1000, DecimalByteUnits, string.Empty, provider);
    }

    /// <summary>Formats a rate given in bytes per second.</summary>
    public static string FormatRate(double bytesPerSecond, DataUnitSystem system, IFormatProvider? provider = null)
    {
        if (double.IsNaN(bytesPerSecond) || double.IsInfinity(bytesPerSecond) || bytesPerSecond < 0)
        {
            return Unavailable;
        }

        return system switch
        {
            DataUnitSystem.BinaryBytes => Scale(bytesPerSecond, 1024, BinaryByteUnits, "/s", provider),
            DataUnitSystem.DecimalBits => Scale(bytesPerSecond * 8, 1000, DecimalBitUnits, "/s", provider),
            _ => Scale(bytesPerSecond, 1000, DecimalByteUnits, "/s", provider),
        };
    }

    /// <summary>Exact representation used in tooltips and exports, e.g. "1,234,567 bytes".</summary>
    public static string FormatExactBytes(long bytes, IFormatProvider? provider = null) =>
        bytes < 0 ? Unavailable : bytes.ToString("N0", provider ?? CultureInfo.CurrentCulture) + " bytes";

    /// <summary>Returns the unit label a rate axis should use for a maximum value (for chart axes).</summary>
    public static (double Divisor, string Unit) ChooseRateUnit(double maxBytesPerSecond, DataUnitSystem system)
    {
        double value = system == DataUnitSystem.DecimalBits ? maxBytesPerSecond * 8 : maxBytesPerSecond;
        double factor = system == DataUnitSystem.BinaryBytes ? 1024 : 1000;
        string[] units = system switch
        {
            DataUnitSystem.BinaryBytes => BinaryByteUnits,
            DataUnitSystem.DecimalBits => DecimalBitUnits,
            _ => DecimalByteUnits,
        };

        int index = 0;
        double divisor = 1;
        while (index < units.Length - 1 && value / divisor >= 999.5)
        {
            divisor *= factor;
            index++;
        }

        // Expressed in bytes-per-second terms so callers can divide raw byte rates directly.
        double byteDivisor = system == DataUnitSystem.DecimalBits ? divisor / 8 : divisor;
        return (byteDivisor, units[index] + "/s");
    }

    /// <summary>Parses quantities such as "500", "1.5MB", "2 MiB", "10k" into bytes (decimal unless 'i' is used).</summary>
    public static bool TryParseBytes(ReadOnlySpan<char> text, out double bytes)
    {
        bytes = 0;
        text = text.Trim();
        if (text.IsEmpty)
        {
            return false;
        }

        int i = 0;
        while (i < text.Length && (char.IsAsciiDigit(text[i]) || text[i] is '.' or ','))
        {
            i++;
        }

        if (i == 0)
        {
            return false;
        }

        string number = text[..i].ToString().Replace(',', '.');
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            return false;
        }

        ReadOnlySpan<char> unit = text[i..].Trim();
        if (unit.EndsWith("/s", StringComparison.OrdinalIgnoreCase))
        {
            unit = unit[..^2];
        }

        bool binary = unit.Length >= 2 && (unit[1] == 'i' || unit[1] == 'I');
        double factor = binary ? 1024 : 1000;
        if (unit.IsEmpty || unit.Equals("b", StringComparison.Ordinal) || unit.Equals("B", StringComparison.Ordinal))
        {
            bytes = value;
            return true;
        }

        char prefix = char.ToUpperInvariant(unit[0]);
        int power = prefix switch
        {
            'K' => 1,
            'M' => 2,
            'G' => 3,
            'T' => 4,
            _ => -1,
        };
        if (power < 0)
        {
            return false;
        }

        bytes = value * Math.Pow(factor, power);
        return true;
    }

    private static string Scale(double value, double factor, string[] units, string suffix, IFormatProvider? provider)
    {
        provider ??= CultureInfo.CurrentCulture;
        int index = 0;
        while (index < units.Length - 1 && value >= 999.5)
        {
            value /= factor;
            index++;
        }

        string number;
        if (index == 0)
        {
            number = Math.Round(value).ToString("0", provider);
        }
        else if (value < 9.995)
        {
            number = value.ToString("0.00", provider);
        }
        else if (value < 99.95)
        {
            number = value.ToString("0.0", provider);
        }
        else
        {
            number = value.ToString("0", provider);
        }

        return number + " " + units[index] + suffix;
    }
}
