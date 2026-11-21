using Microsoft.Extensions.Logging;
using WinNetworkManager.Core.Settings;
using WinNetworkManager.Engine;
using WinNetworkManager.Infrastructure;
using WinNetworkManager.Infrastructure.History;
using WinNetworkManager.Core.Errors;

namespace WinNetworkManager.App.Services;

/// <summary>Owns the history database according to the "persist history" preference.</summary>
public sealed class HistoryService : IDisposable
{
    private readonly AppPaths _paths;
    private readonly MonitoringEngine _engine;
    private readonly ILogger<HistoryService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private SqliteHistoryStore? _store;
    private bool _disposed;

    /// <summary>Creates the service and applies the current preference.</summary>
    public HistoryService(AppPaths paths, MonitoringEngine engine, SettingsService settings, ILoggerFactory loggerFactory)
    {
        _paths = paths;
        _engine = engine;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<HistoryService>();
        Apply(settings.Current);
        settings.Changed += (_, s) => Apply(s);
    }

    /// <summary>The store, or null when persistence is disabled or the database could not be opened.</summary>
    public SqliteHistoryStore? Store => _store;

    /// <summary>Error that prevented opening the database.</summary>
    public string? Error { get; private set; }

    /// <summary>Database location.</summary>
    public string DatabasePath => _paths.HistoryDatabase;

    /// <summary>Deletes all stored history.</summary>
    public void Clear() => _store?.Clear();

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _engine.AttachHistoryStore(null);
        _store?.Dispose();
        _store = null;
    }

    private void Apply(AppSettings settings)
    {
        if (_disposed)
        {
            return;
        }

        if (settings.History.PersistToDisk && _store is null)
        {
            try
            {
                var store = new SqliteHistoryStore(_paths.HistoryDatabase, _loggerFactory.CreateLogger<SqliteHistoryStore>());
                store.Open();
                _store = store;
                Error = null;
                _engine.AttachHistoryStore(store);
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
            {
                Error = ErrorText.Describe(ex);
                _logger.LogError(ex, "The history database could not be opened; history is kept in memory only.");
            }
        }
        else if (!settings.History.PersistToDisk && _store is not null)
        {
            _engine.AttachHistoryStore(null);
            _store.Dispose();
            _store = null;
        }
    }
}
