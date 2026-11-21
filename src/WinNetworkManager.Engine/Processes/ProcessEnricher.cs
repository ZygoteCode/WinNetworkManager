using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using WinNetworkManager.Core.Processes;
using WinNetworkManager.Engine.Model;
using WinNetworkManager.Platform.Windows.Processes;

namespace WinNetworkManager.Engine.Processes;

/// <summary>Result of enriching one process (applied on the engine thread).</summary>
internal sealed record EnrichmentResult(
    ProcessKey Key,
    Probe<string>? Path,
    int? SessionId,
    string? UserName,
    bool? IsServiceAccount,
    FileDetails? File);

/// <summary>Version information and signature of an executable (cached by path).</summary>
internal sealed record FileDetails(string? Description, string? Product, string? Company, string? Version, SignatureView? Signature);

/// <summary>
/// Reads slow per-process details (path, token, version info, Authenticode) on a background worker so the engine loop
/// and the UI never wait for disk or WinVerifyTrust. File-level results are cached per path.
/// </summary>
internal sealed class ProcessEnricher : IAsyncDisposable
{
    private readonly Channel<(ProcessKey Key, string? KnownPath)> _requests =
        Channel.CreateBounded<(ProcessKey, string?)>(new BoundedChannelOptions(4096) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    private readonly ConcurrentQueue<EnrichmentResult> _results = new();
    private readonly ConcurrentDictionary<string, FileDetails> _fileCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;

    public ProcessEnricher(ILogger logger)
    {
        _logger = logger;
        _worker = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>Queues a process for enrichment.</summary>
    public void Request(ProcessKey key, string? knownPath) => _requests.Writer.TryWrite((key, knownPath));

    /// <summary>Dequeues finished results.</summary>
    public bool TryTake(out EnrichmentResult? result) => _results.TryDequeue(out result);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _requests.Writer.TryComplete();
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected
        }

        _cts.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        await foreach (var (key, knownPath) in _requests.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                _results.Enqueue(Enrich(key, knownPath));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogDebug(ex, "Enrichment of {Key} failed.", key);
            }
        }
    }

    private EnrichmentResult Enrich(ProcessKey key, string? knownPath)
    {
        int pid = key.Pid;
        if (WellKnownPids.IsKernel(pid))
        {
            return new EnrichmentResult(key, Probe<string>.Missing(InfoStatus.NotApplicable), 0, @"NT AUTHORITY\SYSTEM", true,
                new FileDetails(pid == WellKnownPids.System ? "NT Kernel & System" : "Idle", "Microsoft® Windows® Operating System", "Microsoft Corporation", null, null));
        }

        // Verify the PID still belongs to this instance before attributing details to it.
        if (key.HasCreateTime)
        {
            var (created, status) = ProcessSnapshotReader.ReadCreateTime(pid);
            if (status == InfoStatus.Available && created != key.CreateTimeTicks)
            {
                return new EnrichmentResult(key, Probe<string>.Missing(InfoStatus.NotFound), null, null, null, null);
            }
        }

        var path = knownPath is not null ? Probe<string>.Of(knownPath) : ProcessDetailsReader.GetImagePath(pid);
        var session = ProcessDetailsReader.GetSessionId(pid);
        var token = ProcessDetailsReader.GetTokenInfo(pid);
        FileDetails? file = null;
        if (path.HasValue)
        {
            file = _fileCache.GetOrAdd(path.Value!, ReadFile);
        }

        return new EnrichmentResult(
            key,
            path,
            session.HasValue ? session.Value : null,
            token.HasValue ? token.Value!.UserName : null,
            token.HasValue ? token.Value!.IsServiceAccount : (bool?)(token.Status == InfoStatus.AccessDenied ? null : false),
            file);
    }

    private static FileDetails ReadFile(string path)
    {
        string? description = null;
        string? product = null;
        string? company = null;
        string? version = null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            description = Clean(info.FileDescription);
            product = Clean(info.ProductName);
            company = Clean(info.CompanyName);
            version = Clean(info.FileVersion);
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Version info is optional.
        }

        SignatureView? signature = null;
        try
        {
            var result = AuthenticodeVerifier.Verify(path);
            signature = new SignatureView(result.Status, result.SignerName, result.IsCatalogSigned, result.Describe());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            signature = new SignatureView(SignatureStatus.FileError, null, false, "File not readable");
        }

        return new FileDetails(description, product, company, version, signature);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
