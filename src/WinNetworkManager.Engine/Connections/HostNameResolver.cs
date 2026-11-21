using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using WinNetworkManager.Core.Networking;

namespace WinNetworkManager.Engine.Connections;

/// <summary>
/// Optional, asynchronous reverse-DNS resolution of remote addresses with a cache and a concurrency limit, so the
/// monitoring loop never waits for DNS. Disabled unless the user enables it (it generates DNS traffic).
/// </summary>
internal sealed class HostNameResolver : IDisposable
{
    private static readonly TimeSpan PositiveTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<IpAddressValue, Entry> _cache = new();
    private readonly SemaphoreSlim _concurrency = new(4, 4);
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Enables or disables resolution.</summary>
    public bool Enabled { get; set; }

    /// <summary>Returns a cached name or schedules a lookup; null when unknown (yet).</summary>
    public string? TryGet(IpAddressValue address)
    {
        if (!Enabled || address.IsEmpty || address.IsUnspecified)
        {
            return null;
        }

        var scope = AddressClassifier.Classify(address);
        if (scope is AddressScope.Loopback or AddressScope.Multicast or AddressScope.Broadcast or AddressScope.Unspecified)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        if (_cache.TryGetValue(address, out var entry) && entry.Expires > now)
        {
            return entry.Name;
        }

        if (_cache.Count > 20_000)
        {
            _cache.Clear();
        }

        _cache[address] = new Entry(null, now + NegativeTtl);
        _ = ResolveAsync(address);
        return entry.Name;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _concurrency.Dispose();
    }

    private async Task ResolveAsync(IpAddressValue address)
    {
        try
        {
            await _concurrency.WaitAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            var host = await Dns.GetHostEntryAsync(address.ToIPAddress().ToString(), timeout.Token).ConfigureAwait(false);
            string? name = string.IsNullOrWhiteSpace(host.HostName) || host.HostName == address.ToString() ? null : host.HostName;
            _cache[address] = new Entry(name, DateTimeOffset.UtcNow + (name is null ? NegativeTtl : PositiveTtl));
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
        {
            // Negative entry already in place.
        }
        finally
        {
            try
            {
                _concurrency.Release();
            }
            catch (ObjectDisposedException)
            {
                // Shutting down.
            }
        }
    }

    private readonly record struct Entry(string? Name, DateTimeOffset Expires);
}
