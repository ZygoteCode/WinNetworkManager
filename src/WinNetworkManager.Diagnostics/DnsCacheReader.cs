using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using WinNetworkManager.Core.Errors;

namespace WinNetworkManager.Diagnostics;

/// <summary>One entry of the Windows DNS client cache.</summary>
/// <param name="Entry">Name that was looked up.</param>
/// <param name="Name">Owner name of the record.</param>
/// <param name="Type">Record type.</param>
/// <param name="Status">Result ("Success", "Name does not exist" for negative entries, …).</param>
/// <param name="Section">Section of the original answer.</param>
/// <param name="TimeToLive">Remaining lifetime in seconds.</param>
/// <param name="Data">Record data, when present.</param>
public sealed record DnsCacheEntry(string Entry, string Name, DnsRecordType Type, string Status, string Section, uint TimeToLive, string? Data)
{
    /// <summary>Type mnemonic.</summary>
    public string TypeText => DnsWire.TypeName(Type);
}

/// <summary>
/// Reads the DNS client cache through the documented DNS Client WMI provider (<c>MSFT_DNSClientCache</c>, the source of
/// <c>Get-DnsClientCache</c>). The cache reveals which names this computer resolved recently; it is read only on request
/// and never stored.
/// </summary>
public static class DnsCacheReader
{
    /// <summary>Reads the cache on a background thread.</summary>
    /// <exception cref="DiagnosticsException">The provider is unavailable or the query failed.</exception>
    public static Task<IReadOnlyList<DnsCacheEntry>> ReadAsync(CancellationToken cancellationToken) =>
        Task.Run(() => Read(cancellationToken), cancellationToken);

    private static IReadOnlyList<DnsCacheEntry> Read(CancellationToken cancellationToken)
    {
        var entries = new List<DnsCacheEntry>();
        try
        {
            var scope = new ManagementScope(@"\\.\root\StandardCimv2");
            var options = new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(15), ReturnImmediately = true, Rewindable = false };
            using var searcher = new ManagementObjectSearcher(
                scope,
                new ObjectQuery("SELECT Entry, Name, Type, Status, Section, TimeToLive, Data FROM MSFT_DNSClientCache"),
                options);
            using var results = searcher.Get();
            foreach (ManagementBaseObject item in results)
            {
                using (item)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    entries.Add(new DnsCacheEntry(
                        item["Entry"] as string ?? string.Empty,
                        item["Name"] as string ?? string.Empty,
                        (DnsRecordType)Convert.ToUInt16(item["Type"] ?? 0, CultureInfo.InvariantCulture),
                        DescribeStatus(Convert.ToUInt32(item["Status"] ?? 0u, CultureInfo.InvariantCulture)),
                        DescribeSection(Convert.ToByte(item["Section"] ?? (byte)0, CultureInfo.InvariantCulture)),
                        Convert.ToUInt32(item["TimeToLive"] ?? 0u, CultureInfo.InvariantCulture),
                        item["Data"] as string));
                }
            }
        }
        catch (ManagementException ex)
        {
            throw new DiagnosticsException("The DNS client cache could not be read: " + ErrorText.Com((int)ex.ErrorCode), ex);
        }
        catch (COMException ex)
        {
            throw new DiagnosticsException("The DNS client cache could not be read: " + ErrorText.Describe(ex), ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new DiagnosticsException("Access to the DNS client cache was denied.", ex);
        }

        return entries;
    }

    internal static string DescribeStatus(uint status) => status switch
    {
        0 => "Success",
        9003 => "Name does not exist",
        9501 => "No records of this type",
        9701 => "No records of this type",
        _ => "Status " + status.ToString(CultureInfo.InvariantCulture),
    };

    internal static string DescribeSection(byte section) => section switch
    {
        1 => "Answer",
        2 => "Authority",
        3 => "Additional",
        _ => "—",
    };
}
