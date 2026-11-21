using System.Globalization;
using WinNetworkManager.Core.Units;

namespace WinNetworkManager.Core.Tests.Units;

public sealed class DataFormatterTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(999, "999 B")]
    [InlineData(1000, "1.00 kB")]
    [InlineData(1499, "1.50 kB")]
    [InlineData(12_345, "12.3 kB")]
    [InlineData(123_456, "123 kB")]
    [InlineData(999_499, "999 kB")]
    [InlineData(999_500, "1.00 MB")]
    [InlineData(1_000_000_000, "1.00 GB")]
    [InlineData(2_500_000_000_000, "2.50 TB")]
    public void Decimal_bytes(long bytes, string expected)
    {
        Assert.Equal(expected, DataFormatter.FormatBytes(bytes, DataUnitSystem.DecimalBytes, Inv));
    }

    [Theory]
    [InlineData(999, "999 B")]
    [InlineData(1000, "0.98 KiB")] // display never exceeds three integer digits
    [InlineData(1023, "1.00 KiB")]
    [InlineData(1024, "1.00 KiB")]
    [InlineData(1536, "1.50 KiB")]
    [InlineData(1_048_576, "1.00 MiB")]
    [InlineData(1_073_741_824, "1.00 GiB")]
    public void Binary_bytes(long bytes, string expected)
    {
        Assert.Equal(expected, DataFormatter.FormatBytes(bytes, DataUnitSystem.BinaryBytes, Inv));
    }

    [Theory]
    [InlineData(0, "0 B/s")]
    [InlineData(512, "512 B/s")]
    [InlineData(1_250_000, "1.25 MB/s")]
    [InlineData(87_654_321, "87.7 MB/s")]
    public void Decimal_rates(double rate, string expected)
    {
        Assert.Equal(expected, DataFormatter.FormatRate(rate, DataUnitSystem.DecimalBytes, Inv));
    }

    [Theory]
    [InlineData(125_000, "1.00 Mbit/s")]
    [InlineData(12_500, "100 kbit/s")]
    [InlineData(100, "800 bit/s")]
    [InlineData(125_000_000, "1.00 Gbit/s")]
    public void Bit_rates(double bytesPerSecond, string expected)
    {
        Assert.Equal(expected, DataFormatter.FormatRate(bytesPerSecond, DataUnitSystem.DecimalBits, Inv));
    }

    [Fact]
    public void Invalid_values_render_as_unavailable_never_as_zero()
    {
        Assert.Equal(DataFormatter.Unavailable, DataFormatter.FormatBytes(-1, DataUnitSystem.DecimalBytes, Inv));
        Assert.Equal(DataFormatter.Unavailable, DataFormatter.FormatRate(double.NaN, DataUnitSystem.DecimalBytes, Inv));
        Assert.Equal(DataFormatter.Unavailable, DataFormatter.FormatRate(-5, DataUnitSystem.DecimalBytes, Inv));
        Assert.Equal(DataFormatter.Unavailable, DataFormatter.FormatRate(double.PositiveInfinity, DataUnitSystem.DecimalBytes, Inv));
    }

    [Fact]
    public void Uses_culture_decimal_separator()
    {
        Assert.Equal("1,25 MB/s", DataFormatter.FormatRate(1_250_000, DataUnitSystem.DecimalBytes, CultureInfo.GetCultureInfo("it-IT")));
    }

    [Theory]
    [InlineData("500", 500)]
    [InlineData("1kB", 1000)]
    [InlineData("1.5MB", 1_500_000)]
    [InlineData("2 MiB", 2_097_152)]
    [InlineData("10k", 10_000)]
    [InlineData("1,5MB/s", 1_500_000)]
    [InlineData("3G", 3_000_000_000)]
    public void Parses_quantities(string text, double expected)
    {
        Assert.True(DataFormatter.TryParseBytes(text, out double bytes));
        Assert.Equal(expected, bytes, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("MB")]
    [InlineData("12 parsecs")]
    public void Rejects_bad_quantities(string text)
    {
        Assert.False(DataFormatter.TryParseBytes(text, out _));
    }

    [Theory]
    [InlineData(500, DataUnitSystem.DecimalBytes, 1, "B/s")]
    [InlineData(5_000_000, DataUnitSystem.DecimalBytes, 1_000_000, "MB/s")]
    [InlineData(5_000_000, DataUnitSystem.BinaryBytes, 1_048_576, "MiB/s")]
    [InlineData(5_000_000, DataUnitSystem.DecimalBits, 125_000, "Mbit/s")]
    public void Chooses_axis_units(double max, DataUnitSystem system, double divisor, string unit)
    {
        var (d, u) = DataFormatter.ChooseRateUnit(max, system);
        Assert.Equal(divisor, d, 6);
        Assert.Equal(unit, u);
    }
}
