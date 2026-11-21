using WinNetworkManager.Core.Querying;
using WinNetworkManager.Core.Settings;
using WinNetworkManager.Core.Units;

namespace WinNetworkManager.Core.Tests;

public sealed class QueryAndSettingsTests
{
    private static readonly HashSet<string> Keys = new(StringComparer.OrdinalIgnoreCase) { "pid", "port", "state", "ip", "rate", "name" };

    [Fact]
    public void Parses_terms_filters_negation_and_phrases()
    {
        var q = SearchQueryParser.Parse("chrome port:443 -state:listen \"Google LLC\" rate>=1MB", Keys);
        Assert.Equal(["chrome", "Google LLC"], q.Terms.Select(t => t.Text));
        Assert.Equal(3, q.Filters.Count);
        Assert.Equal(new SearchFilter("port", FilterOperator.Matches, "443", false), q.Filters[0]);
        Assert.Equal(new SearchFilter("state", FilterOperator.Matches, "listen", true), q.Filters[1]);
        Assert.Equal(new SearchFilter("rate", FilterOperator.GreaterOrEqual, "1MB", false), q.Filters[2]);
    }

    [Theory]
    [InlineData("fe80::1")]
    [InlineData("2001:db8::1")]
    [InlineData("unknown:value")]
    [InlineData("port:")]
    public void Ambiguous_tokens_are_free_text(string token)
    {
        var q = SearchQueryParser.Parse(token, Keys);
        Assert.Empty(q.Filters);
        Assert.Single(q.Terms);
    }

    [Fact]
    public void Evaluate_requires_all_terms_and_filters_and_ignores_unknown_filters()
    {
        var q = SearchQueryParser.Parse("svc -edge pid=10", Keys);
        bool Match(string text, string subject) => subject.Contains(text, StringComparison.OrdinalIgnoreCase);
        Assert.True(q.Evaluate(t => Match(t, "svchost.exe"), f => f.Key == "pid" ? f.Value == "10" : null));
        Assert.False(q.Evaluate(t => Match(t, "svchost.exe msedge"), f => f.Key == "pid" ? f.Value == "10" : null));
        Assert.False(q.Evaluate(t => Match(t, "svchost.exe"), f => f.Key == "pid" ? f.Value == "11" : null));
        Assert.True(q.Evaluate(t => Match(t, "svchost.exe"), _ => null));
    }

    [Fact]
    public void Empty_query()
    {
        Assert.True(SearchQueryParser.Parse("   ", Keys).IsEmpty);
        Assert.True(SearchQueryParser.Parse(null, Keys).IsEmpty);
    }

    [Fact]
    public void Default_settings_are_valid()
    {
        var result = SettingsValidator.Validate(new AppSettings());
        Assert.True(result.IsValid, string.Join("; ", result.Issues));
    }

    [Fact]
    public void Out_of_range_values_are_clamped_and_reported()
    {
        var bad = new AppSettings
        {
            Monitoring = new MonitoringSettings { RefreshIntervalMs = 10, ProcessPollIntervalMs = 1_000_000 },
            Appearance = new AppearanceSettings { AccentColor = "blue", TextScale = 7, Units = (DataUnitSystem)99 },
            History = new HistorySettings { RetentionDays = 0 },
            Export = new ExportSettings { CsvDelimiter = '|' },
        };

        var result = SettingsValidator.Validate(bad);
        Assert.False(result.IsValid);
        Assert.Equal(250, result.Settings.Monitoring.RefreshIntervalMs);
        Assert.Equal(30_000, result.Settings.Monitoring.ProcessPollIntervalMs);
        Assert.Null(result.Settings.Appearance.AccentColor);
        Assert.Equal(2.0, result.Settings.Appearance.TextScale);
        Assert.Equal(DataUnitSystem.DecimalBytes, result.Settings.Appearance.Units);
        Assert.Equal(1, result.Settings.History.RetentionDays);
        Assert.Equal(',', result.Settings.Export.CsvDelimiter);
        Assert.Equal(7, result.Issues.Count);
    }

    [Fact]
    public void Null_sections_are_replaced_with_defaults()
    {
        var broken = new AppSettings { Monitoring = null!, Appearance = null!, Tables = null! };
        var result = SettingsValidator.Validate(broken);
        Assert.NotNull(result.Settings.Monitoring);
        Assert.NotNull(result.Settings.Appearance);
        Assert.NotNull(result.Settings.Tables);
    }

    [Fact]
    public void Hidden_process_names_are_trimmed_and_deduplicated()
    {
        var s = new AppSettings { Processes = new ProcessListSettings { HiddenProcessNames = [" svchost.exe", "SVCHOST.EXE", "", "  "] } };
        var result = SettingsValidator.Validate(s);
        Assert.Equal(["svchost.exe"], result.Settings.Processes.HiddenProcessNames);
    }

    [Fact]
    public void Table_layouts_drop_invalid_columns()
    {
        var s = new AppSettings
        {
            Tables = new Dictionary<string, TableLayout>
            {
                ["processes"] = new()
                {
                    Columns = [new ColumnLayout("name", true, 200), new ColumnLayout("name", false, 10), new ColumnLayout("", true, null), new ColumnLayout("pid", true, -3)],
                },
            },
        };

        var columns = SettingsValidator.Validate(s).Settings.Tables["processes"].Columns;
        Assert.Equal(2, columns.Count);
        Assert.Equal(200, columns[0].Width);
        Assert.Null(columns[1].Width);
    }

    [Theory]
    [InlineData("#00AAFF", true)]
    [InlineData("#00aaff", true)]
    [InlineData("00AAFF", false)]
    [InlineData("#00AAF", false)]
    [InlineData("#GGAAFF", false)]
    public void Hex_colors(string value, bool expected)
    {
        Assert.Equal(expected, SettingsValidator.IsHexColor(value));
    }
}
