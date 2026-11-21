namespace WinNetworkManager.Infrastructure;

/// <summary>
/// Locations of user data. Default: <c>%LOCALAPPDATA%\WinNetworkManager</c>. Portable mode: when a file named
/// <c>portable.txt</c> sits next to the executable, data lives in <c>Data\</c> beside it. The environment variable
/// <c>WNM_DATA_DIR</c> overrides both (tests, diagnostics).
/// </summary>
public sealed class AppPaths
{
    /// <summary>Marker file enabling portable mode.</summary>
    public const string PortableMarker = "portable.txt";

    /// <summary>Creates paths rooted at <paramref name="dataDirectory"/>.</summary>
    public AppPaths(string dataDirectory, bool isPortable = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);
        DataDirectory = Path.GetFullPath(dataDirectory);
        IsPortable = isPortable;
    }

    /// <summary>Root of user data.</summary>
    public string DataDirectory { get; }

    /// <summary>True in portable mode.</summary>
    public bool IsPortable { get; }

    /// <summary>Settings file.</summary>
    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    /// <summary>History database.</summary>
    public string HistoryDatabase => Path.Combine(DataDirectory, "history.db");

    /// <summary>Log directory.</summary>
    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>Resolves the paths for the current process.</summary>
    public static AppPaths Resolve()
    {
        string? overrideDir = Environment.GetEnvironmentVariable("WNM_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir))
        {
            return new AppPaths(overrideDir);
        }

        string baseDir = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(baseDir, PortableMarker)))
        {
            return new AppPaths(Path.Combine(baseDir, "Data"), isPortable: true);
        }

        return new AppPaths(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinNetworkManager"));
    }

    /// <summary>Creates the directories.</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
    }
}
