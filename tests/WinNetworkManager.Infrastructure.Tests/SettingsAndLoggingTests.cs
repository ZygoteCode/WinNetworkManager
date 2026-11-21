using System.Text.Json;
using Microsoft.Extensions.Logging;
using WinNetworkManager.Core.Settings;
using WinNetworkManager.Core.Units;
using WinNetworkManager.Infrastructure;
using WinNetworkManager.Infrastructure.Logging;
using WinNetworkManager.Infrastructure.Settings;

namespace WinNetworkManager.Infrastructure.Tests;

public sealed class SettingsAndLoggingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wnm-tests-" + Guid.NewGuid().ToString("N"));

    public SettingsAndLoggingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Missing_file_loads_defaults_without_issues()
    {
        var store = new JsonSettingsStore(Path.Combine(_dir, "settings.json"));
        var result = store.Load();
        Assert.Empty(result.Issues);
        Assert.Equal(new AppSettings().Monitoring.RefreshIntervalMs, result.Settings.Monitoring.RefreshIntervalMs);
    }

    [Fact]
    public void Save_and_reload_round_trips_and_raises_changed()
    {
        string path = Path.Combine(_dir, "settings.json");
        var store = new JsonSettingsStore(path);
        store.Load();
        AppSettings? changed = null;
        store.Changed += (_, s) => changed = s;

        var saved = store.Update(s => s with
        {
            Appearance = s.Appearance with { Theme = ThemePreference.Dark, Units = DataUnitSystem.BinaryBytes, AccentColor = "#12AB34" },
            Monitoring = s.Monitoring with { RefreshIntervalMs = 2000, IncludeLoopbackInTotals = true },
            Processes = s.Processes with { HiddenProcessNames = ["svchost.exe"] },
            Tables = new Dictionary<string, TableLayout> { ["processes"] = new() { Columns = [new ColumnLayout("name", true, 220)], SortColumn = "download" } },
        });

        Assert.NotNull(changed);
        var reloaded = new JsonSettingsStore(path).Load().Settings;
        Assert.Equal(ThemePreference.Dark, reloaded.Appearance.Theme);
        Assert.Equal(DataUnitSystem.BinaryBytes, reloaded.Appearance.Units);
        Assert.Equal("#12AB34", reloaded.Appearance.AccentColor);
        Assert.Equal(2000, reloaded.Monitoring.RefreshIntervalMs);
        Assert.True(reloaded.Monitoring.IncludeLoopbackInTotals);
        Assert.Equal(["svchost.exe"], reloaded.Processes.HiddenProcessNames);
        Assert.Equal("download", reloaded.Tables["processes"].SortColumn);
        Assert.Equal(220, reloaded.Tables["processes"].Columns[0].Width);
        Assert.Equal(saved, store.Current);

        string json = File.ReadAllText(path);
        Assert.Contains("\"theme\": \"Dark\"", json, StringComparison.Ordinal); // enums as strings, human editable
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Corrupted_file_is_preserved_and_defaults_are_used()
    {
        string path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{ this is not json");
        var result = new JsonSettingsStore(path).Load();
        Assert.Single(result.Issues);
        Assert.Contains(Directory.GetFiles(_dir), f => Path.GetFileName(f).StartsWith("settings.json.corrupt-", StringComparison.Ordinal));
        Assert.Equal(new AppSettings().Monitoring.RefreshIntervalMs, result.Settings.Monitoring.RefreshIntervalMs);
    }

    [Fact]
    public void Out_of_range_values_in_file_are_clamped_and_reported()
    {
        string path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """
            {
              // comments are allowed for hand-edited files
              "monitoring": { "refreshIntervalMs": 1 },
              "appearance": { "theme": "Dark", "textScale": 9 },
              "unknownSection": { "x": 1 },
            }
            """);
        var result = new JsonSettingsStore(path).Load();
        Assert.Equal(250, result.Settings.Monitoring.RefreshIntervalMs);
        Assert.Equal(2.0, result.Settings.Appearance.TextScale);
        Assert.Equal(ThemePreference.Dark, result.Settings.Appearance.Theme);
        Assert.True(result.Issues.Count == 2, string.Join(" | ", result.Issues));
        Assert.Equal(1000, result.Settings.Monitoring.ConnectionPollIntervalMs); // missing value keeps its default
    }

    [Fact]
    public void Unknown_enum_text_is_a_load_issue_not_a_crash()
    {
        string path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """{ "appearance": { "theme": "Purple" } }""");
        var result = new JsonSettingsStore(path).Load();
        Assert.NotEmpty(result.Issues);
        Assert.Equal(ThemePreference.System, result.Settings.Appearance.Theme);
    }

    [Fact]
    public void Paths_honour_override_and_create_directories()
    {
        var paths = new AppPaths(Path.Combine(_dir, "data"));
        paths.EnsureCreated();
        Assert.True(Directory.Exists(paths.LogDirectory));
        Assert.EndsWith("settings.json", paths.SettingsFile, StringComparison.Ordinal);
        Assert.EndsWith("history.db", paths.HistoryDatabase, StringComparison.Ordinal);
    }

    [Fact]
    public async Task File_logger_writes_json_lines_respects_level_and_retention()
    {
        string logs = Path.Combine(_dir, "logs");
        for (int i = 0; i < 5; i++)
        {
            Directory.CreateDirectory(logs);
            string old = Path.Combine(logs, $"wnm-2000010{i}.jsonl");
            File.WriteAllText(old, "{}");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-10 - i));
        }

        using (var provider = new JsonLinesFileLoggerProvider(new FileLoggerOptions(logs, LogLevel.Information, RetainedFiles: 3)))
        {
            var logger = provider.CreateLogger("Test.Category");
            logger.LogDebug("hidden");
            logger.LogInformation("Hello {Name}", "world \"quoted\"");
            logger.LogError(new InvalidOperationException("boom"), "Failure {Code}", 42);
            await provider.FlushAsync();
        }

        var files = Directory.GetFiles(logs, "wnm-*.jsonl");
        Assert.Equal(3, files.Length);
        string today = Path.Combine(logs, $"wnm-{DateTime.UtcNow:yyyyMMdd}.jsonl");
        var lines = File.ReadAllLines(today);
        Assert.Equal(2, lines.Length);
        using var first = JsonDocument.Parse(lines[0]);
        Assert.Equal("Information", first.RootElement.GetProperty("level").GetString());
        Assert.Equal("Test.Category", first.RootElement.GetProperty("category").GetString());
        Assert.Equal("Hello world \"quoted\"", first.RootElement.GetProperty("message").GetString());
        using var second = JsonDocument.Parse(lines[1]);
        Assert.Contains("boom", second.RootElement.GetProperty("exception").GetString(), StringComparison.Ordinal);
    }
}
