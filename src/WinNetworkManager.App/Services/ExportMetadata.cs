using System.Globalization;
using WinNetworkManager.Core.Settings;
using WinNetworkManager.Engine.Model;

namespace WinNetworkManager.App.Services;

/// <summary>Provenance written into every export so values can be interpreted without the application.</summary>
public static class ExportMetadata
{
    /// <summary>Builds the metadata for an export of <paramref name="page"/>.</summary>
    public static IReadOnlyDictionary<string, string> Create(AppSettings settings, EngineSnapshot snapshot, string page)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(snapshot);
        return new Dictionary<string, string>
        {
            ["application"] = "WinNetworkManager " + (typeof(ExportMetadata).Assembly.GetName().Version?.ToString(3) ?? "?"),
            ["view"] = page,
            ["generated_utc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["snapshot_utc"] = snapshot.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["units"] = "bytes and bytes per second; values are raw (not scaled to kB/MB)",
            ["timestamps"] = "UTC, ISO 8601",
            ["rate_window_seconds"] = snapshot.RateWindow.TotalSeconds.ToString(CultureInfo.InvariantCulture),
            ["loopback_included_in_totals"] = settings.Monitoring.IncludeLoopbackInTotals ? "true" : "false",
            ["collector_state"] = snapshot.Collector.State.ToString(),
            ["collector_source"] = snapshot.Collector.Kind.ToString(),
            ["traffic_data_as_of_utc"] = snapshot.Collector.DataAsOf?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
            ["partial_data_in_window"] = snapshot.Collector.PartialData ? "true" : "false",
            ["events_lost_total"] = snapshot.Collector.EventsLost.ToString(CultureInfo.InvariantCulture),
            ["source_process_traffic"] = "ETW Microsoft-Windows-Kernel-Network: TCP/UDP payload bytes per process (no IP/TCP/UDP headers, retransmissions separate)",
            ["source_connections"] = "IP Helper GetExtendedTcpTable / GetExtendedUdpTable (TCP_TABLE_OWNER_MODULE_ALL, UDP_TABLE_OWNER_MODULE), IPv4 and IPv6",
            ["source_interfaces"] = "IP Helper GetIfTable2 octet counters (link layer, all protocols, headers included)",
        };
    }
}
