using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using WinNetworkManager.Core.Errors;

namespace WinNetworkManager.Diagnostics;

/// <summary>HTTP/TLS probe parameters.</summary>
public sealed record HttpProbeSettings
{
    /// <summary>URL to probe (http or https).</summary>
    public required Uri Url { get; init; }

    /// <summary>"HEAD" (default) or "GET". Only the response header is read in both cases.</summary>
    public string Method { get; init; } = "HEAD";

    /// <summary>Time limit for each request (DNS + connect + TLS + response header).</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Follow 301/302/303/307/308 redirects.</summary>
    public bool FollowRedirects { get; init; }

    /// <summary>Maximum redirects followed.</summary>
    public int MaxRedirects { get; init; } = 5;

    /// <summary>Check certificate revocation online (contacts the certificate authority's CRL/OCSP servers).</summary>
    public bool CheckRevocation { get; init; }
}

/// <summary>Server certificate as seen during the handshake.</summary>
/// <param name="Subject">Subject distinguished name.</param>
/// <param name="Issuer">Issuer distinguished name.</param>
/// <param name="NotBefore">Start of validity.</param>
/// <param name="NotAfter">End of validity.</param>
/// <param name="Sha256Fingerprint">SHA-256 fingerprint (hex).</param>
/// <param name="SerialNumber">Serial number (hex).</param>
/// <param name="SubjectAlternativeNames">DNS names and IP addresses the certificate is valid for.</param>
/// <param name="KeyAlgorithm">Public key algorithm.</param>
/// <param name="KeySize">Key size in bits.</param>
/// <param name="SignatureAlgorithm">Signature algorithm.</param>
/// <param name="Chain">Chain from the server certificate to the root, as built by Windows.</param>
/// <param name="ChainStatus">Chain problems reported by Windows.</param>
/// <param name="PolicyErrors">Validation result.</param>
public sealed record CertificateInfo(
    string Subject,
    string Issuer,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    string Sha256Fingerprint,
    string SerialNumber,
    IReadOnlyList<string> SubjectAlternativeNames,
    string KeyAlgorithm,
    int? KeySize,
    string SignatureAlgorithm,
    IReadOnlyList<string> Chain,
    IReadOnlyList<string> ChainStatus,
    SslPolicyErrors PolicyErrors)
{
    /// <summary>True when Windows accepted the certificate for this host.</summary>
    public bool IsValid => PolicyErrors == SslPolicyErrors.None;

    /// <summary>Validation text.</summary>
    public string ValidationText
    {
        get
        {
            if (PolicyErrors == SslPolicyErrors.None)
            {
                return "Valid: trusted chain and matching host name";
            }

            var problems = new List<string>();
            if (PolicyErrors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
            {
                problems.Add("the server presented no certificate");
            }

            if (PolicyErrors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
            {
                problems.Add("the certificate is not valid for this host name");
            }

            if (PolicyErrors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
            {
                problems.Add("the certificate chain is not trusted (see chain status)");
            }

            return "Invalid: " + string.Join("; ", problems);
        }
    }

    /// <summary>English text for a chain problem (Windows localizes <see cref="X509ChainStatus.StatusInformation"/>).</summary>
    internal static string DescribeChainStatus(X509ChainStatusFlags status) => status switch
    {
        X509ChainStatusFlags.NotTimeValid => "A certificate in the chain has expired or is not yet valid",
        X509ChainStatusFlags.Revoked => "A certificate in the chain has been revoked",
        X509ChainStatusFlags.NotSignatureValid => "A certificate signature is not valid",
        X509ChainStatusFlags.NotValidForUsage => "A certificate is not valid for server authentication",
        X509ChainStatusFlags.UntrustedRoot => "The chain ends in a root certificate that is not trusted",
        X509ChainStatusFlags.RevocationStatusUnknown => "The revocation status could not be determined",
        X509ChainStatusFlags.Cyclic => "The certificate chain is cyclic",
        X509ChainStatusFlags.InvalidExtension => "A certificate has an invalid extension",
        X509ChainStatusFlags.InvalidPolicyConstraints or X509ChainStatusFlags.InvalidBasicConstraints or X509ChainStatusFlags.InvalidNameConstraints
            or X509ChainStatusFlags.HasNotSupportedNameConstraint or X509ChainStatusFlags.HasNotDefinedNameConstraint
            or X509ChainStatusFlags.HasNotPermittedNameConstraint or X509ChainStatusFlags.HasExcludedNameConstraint => "A certificate violates a constraint of its issuer",
        X509ChainStatusFlags.PartialChain => "The chain could not be built up to a trusted root (an intermediate certificate is missing)",
        X509ChainStatusFlags.CtlNotTimeValid or X509ChainStatusFlags.CtlNotSignatureValid or X509ChainStatusFlags.CtlNotValidForUsage => "A certificate trust list is not valid",
        X509ChainStatusFlags.OfflineRevocation => "The revocation server could not be reached",
        X509ChainStatusFlags.NoIssuanceChainPolicy => "No issuance policy is present",
        X509ChainStatusFlags.ExplicitDistrust => "A certificate in the chain is explicitly distrusted",
        X509ChainStatusFlags.HasNotSupportedCriticalExtension => "A certificate has an unsupported critical extension",
        X509ChainStatusFlags.HasWeakSignature => "A certificate uses a weak signature algorithm",
        _ => status.ToString(),
    };

    internal static CertificateInfo Create(X509Certificate certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        var cert = certificate as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        try
        {
            var names = new List<string>();
            foreach (var extension in cert.Extensions)
            {
                if (extension.Oid?.Value != "2.5.29.17")
                {
                    continue;
                }

                try
                {
                    var san = extension as X509SubjectAlternativeNameExtension ?? new X509SubjectAlternativeNameExtension(extension.RawData, extension.Critical);
                    names.AddRange(san.EnumerateDnsNames().Select(n => "DNS: " + n));
                    names.AddRange(san.EnumerateIPAddresses().Select(a => "IP: " + a));
                }
                catch (CryptographicException)
                {
                    names.Add("(unreadable subject alternative name extension)");
                }
            }

            int? keySize = null;
            using (var rsa = cert.GetRSAPublicKey())
            {
                keySize ??= rsa?.KeySize;
            }

            using (var ecdsa = cert.GetECDsaPublicKey())
            {
                keySize ??= ecdsa?.KeySize;
            }

            var elements = new List<string>();
            var status = new List<string>();
            if (chain is not null)
            {
                foreach (var element in chain.ChainElements)
                {
                    elements.Add(element.Certificate.Subject);
                }

                foreach (var s in chain.ChainStatus)
                {
                    status.Add(DescribeChainStatus(s.Status));
                }
            }

            return new CertificateInfo(
                cert.Subject,
                cert.Issuer,
                new DateTimeOffset(cert.NotBefore),
                new DateTimeOffset(cert.NotAfter),
                cert.GetCertHashString(HashAlgorithmName.SHA256),
                cert.SerialNumber,
                names,
                cert.PublicKey.Oid.FriendlyName ?? cert.PublicKey.Oid.Value ?? "?",
                keySize,
                cert.SignatureAlgorithm.FriendlyName ?? cert.SignatureAlgorithm.Value ?? "?",
                elements,
                status,
                errors);
        }
        finally
        {
            if (!ReferenceEquals(cert, certificate))
            {
                cert.Dispose();
            }
        }
    }
}

/// <summary>A response header field.</summary>
/// <param name="Name">Field name.</param>
/// <param name="Value">Field value.</param>
public sealed record HttpHeader(string Name, string Value);

/// <summary>One request of an HTTP probe (several when redirects are followed).</summary>
public sealed record HttpProbeStep
{
    /// <summary>URL requested.</summary>
    public required Uri Url { get; init; }

    /// <summary>Server endpoint connected to.</summary>
    public IPEndPoint? Remote { get; init; }

    /// <summary>Name resolution time (null for address literals).</summary>
    public double? DnsMs { get; init; }

    /// <summary>TCP handshake time.</summary>
    public double? ConnectMs { get; init; }

    /// <summary>TLS handshake time (https only).</summary>
    public double? TlsMs { get; init; }

    /// <summary>Time from sending the request to the first byte of the response (server processing + one round trip).</summary>
    public double? WaitMs { get; init; }

    /// <summary>Time from the start of the request to the end of the response header.</summary>
    public double? TotalMs { get; init; }

    /// <summary>HTTP status code.</summary>
    public int? StatusCode { get; init; }

    /// <summary>Reason phrase.</summary>
    public string? ReasonPhrase { get; init; }

    /// <summary>Protocol version of the response ("HTTP/1.1").</summary>
    public string? HttpVersion { get; init; }

    /// <summary>Response header fields, in order.</summary>
    public IReadOnlyList<HttpHeader> Headers { get; init; } = [];

    /// <summary>Negotiated TLS version.</summary>
    public SslProtocols? TlsProtocol { get; init; }

    /// <summary>Negotiated cipher suite.</summary>
    public TlsCipherSuite? CipherSuite { get; init; }

    /// <summary>Negotiated ALPN protocol.</summary>
    public string? ApplicationProtocol { get; init; }

    /// <summary>Server certificate (also when validation failed).</summary>
    public CertificateInfo? Certificate { get; init; }

    /// <summary>Failure text, or null.</summary>
    public string? Error { get; init; }

    /// <summary>Stage that failed ("DNS", "Connect", "TLS", "HTTP").</summary>
    public string? FailedStage { get; init; }

    /// <summary>Location header, when present.</summary>
    public string? Location => Headers.FirstOrDefault(h => h.Name.Equals("Location", StringComparison.OrdinalIgnoreCase))?.Value;

    /// <summary>True when a response header was received.</summary>
    public bool Succeeded => Error is null;

    /// <summary>TLS version text.</summary>
    public string TlsProtocolText => TlsProtocol switch
    {
        null => "—",
        SslProtocols.Tls13 => "TLS 1.3",
        SslProtocols.Tls12 => "TLS 1.2",
        { } other => other.ToString(),
    };
}

/// <summary>Result of an HTTP probe.</summary>
/// <param name="Steps">Requests made (one per redirect).</param>
public sealed record HttpProbeResult(IReadOnlyList<HttpProbeStep> Steps)
{
    /// <summary>Last request.</summary>
    public HttpProbeStep Final => Steps[^1];
}

/// <summary>
/// Measures an HTTP(S) request stage by stage on a single connection: DNS, TCP connect, TLS handshake (version, cipher,
/// ALPN, certificate and chain as validated by Windows), and the response header. HTTP/1.1 only; the body is not
/// downloaded. Certificate validation is never bypassed: an invalid certificate is reported and the request stops.
/// </summary>
public static class HttpProbeTool
{
    private const int MaxHeaderBytes = 64 * 1024;

    /// <summary>Runs the probe.</summary>
    public static async Task<HttpProbeResult> ProbeAsync(HttpProbeSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Validate(settings);
        var steps = new List<HttpProbeStep>();
        var url = settings.Url;
        int limit = settings.FollowRedirects ? Math.Clamp(settings.MaxRedirects, 0, 20) : 0;
        for (int i = 0; i <= limit; i++)
        {
            var step = await ProbeOnceAsync(url, settings, cancellationToken).ConfigureAwait(false);
            steps.Add(step);
            if (!settings.FollowRedirects || step.StatusCode is not (301 or 302 or 303 or 307 or 308) || step.Location is not { } location)
            {
                break;
            }

            if (!Uri.TryCreate(url, location, out var next) || next.Scheme is not ("http" or "https"))
            {
                break;
            }

            url = next;
        }

        return new HttpProbeResult(steps);
    }

    private static void Validate(HttpProbeSettings settings)
    {
        if (!settings.Url.IsAbsoluteUri || settings.Url.Scheme is not ("http" or "https"))
        {
            throw new DiagnosticsException("Enter an http:// or https:// URL.");
        }

        if (settings.Method is not ("HEAD" or "GET"))
        {
            throw new DiagnosticsException("The method must be HEAD or GET.");
        }
    }

    private static async Task<HttpProbeStep> ProbeOnceAsync(Uri url, HttpProbeSettings settings, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(settings.Timeout);
        var token = timeoutSource.Token;
        long started = Stopwatch.GetTimestamp();
        string stage = "DNS";
        double? dnsMs = null, connectMs = null, tlsMs = null, waitMs = null;
        IPEndPoint? remote = null;
        CertificateInfo? certificate = null;
        SslProtocols? protocol = null;
        TlsCipherSuite? cipher = null;
        string? alpn = null;
        Stream? stream = null;
        try
        {
            bool https = url.Scheme == "https";
            string host = url.HostNameType is UriHostNameType.IPv6 or UriHostNameType.IPv4 ? url.DnsSafeHost : url.IdnHost;
            IPAddress[] addresses;
            if (IPAddress.TryParse(host, out var literal))
            {
                addresses = [literal];
            }
            else
            {
                long dnsStart = Stopwatch.GetTimestamp();
                try
                {
                    addresses = await Dns.GetHostAddressesAsync(host, token).ConfigureAwait(false);
                }
                catch (SocketException ex)
                {
                    throw new DiagnosticsException(DiagnosticTarget.DescribeResolverError(host, ex), ex);
                }

                dnsMs = Stopwatch.GetElapsedTime(dnsStart).TotalMilliseconds;
                if (addresses.Length == 0)
                {
                    throw new DiagnosticsException($"{host} resolved to no address.");
                }
            }

            stage = "Connect";
            Socket? socket = null;
            SocketException? lastError = null;
            foreach (var address in addresses.Take(4))
            {
                var candidate = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                long connectStart = Stopwatch.GetTimestamp();
                try
                {
                    await candidate.ConnectAsync(new IPEndPoint(address, url.Port), token).ConfigureAwait(false);
                    connectMs = Stopwatch.GetElapsedTime(connectStart).TotalMilliseconds;
                    remote = new IPEndPoint(address, url.Port);
                    socket = candidate;
                    break;
                }
                catch (SocketException ex)
                {
                    lastError = ex;
                    candidate.Dispose();
                }
                catch
                {
                    candidate.Dispose();
                    throw;
                }
            }

            if (socket is null)
            {
                throw lastError is null
                    ? new DiagnosticsException("No address to connect to.")
                    : new DiagnosticsException("The connection failed: " + ErrorText.Describe(lastError), lastError);
            }

            stream = new NetworkStream(socket, ownsSocket: true);
            if (https)
            {
                stage = "TLS";
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                stream = ssl;
                var options = new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                    ApplicationProtocols = [SslApplicationProtocol.Http11],
                    CertificateRevocationCheckMode = settings.CheckRevocation ? X509RevocationMode.Online : X509RevocationMode.NoCheck,
                    RemoteCertificateValidationCallback = (_, cert, chain, errors) =>
                    {
                        if (cert is not null)
                        {
                            certificate = CertificateInfo.Create(cert, chain, errors);
                        }

                        return errors == SslPolicyErrors.None;
                    },
                };
                long tlsStart = Stopwatch.GetTimestamp();
                await ssl.AuthenticateAsClientAsync(options, token).ConfigureAwait(false);
                tlsMs = Stopwatch.GetElapsedTime(tlsStart).TotalMilliseconds;
                protocol = ssl.SslProtocol;
                cipher = ssl.NegotiatedCipherSuite;
                alpn = ssl.NegotiatedApplicationProtocol.Protocol.Length > 0 ? ssl.NegotiatedApplicationProtocol.ToString() : null;
            }

            stage = "HTTP";
            byte[] request = BuildRequest(settings.Method, url);
            long sent = Stopwatch.GetTimestamp();
            await stream.WriteAsync(request, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
            var (header, firstByte) = await ReadHeaderAsync(stream, token).ConfigureAwait(false);
            waitMs = Stopwatch.GetElapsedTime(sent, firstByte).TotalMilliseconds;
            var (version, status, reason, headers) = ParseHeader(header);
            return new HttpProbeStep
            {
                Url = url,
                Remote = remote,
                DnsMs = dnsMs,
                ConnectMs = connectMs,
                TlsMs = tlsMs,
                WaitMs = waitMs,
                TotalMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                StatusCode = status,
                ReasonPhrase = reason,
                HttpVersion = version,
                Headers = headers,
                TlsProtocol = protocol,
                CipherSuite = cipher,
                ApplicationProtocol = alpn,
                Certificate = certificate,
            };
        }
        catch (Exception ex) when (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return Failed(string.Create(CultureInfo.CurrentCulture, $"Timed out after {settings.Timeout.TotalSeconds:0.#} s during {Stage(stage)}."));
        }
        catch (AuthenticationException ex)
        {
            string detail = certificate is { IsValid: false } c ? c.ValidationText : ErrorText.Describe(ex);
            return Failed("The TLS handshake failed: " + detail);
        }
        catch (Exception ex) when (ex is DiagnosticsException or IOException or SocketException)
        {
            return Failed(ErrorText.Describe(ex));
        }
        finally
        {
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }

        HttpProbeStep Failed(string error) => new()
        {
            Url = url,
            Remote = remote,
            DnsMs = dnsMs,
            ConnectMs = connectMs,
            TlsMs = tlsMs,
            WaitMs = waitMs,
            TotalMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            TlsProtocol = protocol,
            CipherSuite = cipher,
            ApplicationProtocol = alpn,
            Certificate = certificate,
            Error = error,
            FailedStage = stage,
        };
    }

    private static string Stage(string stage) => stage switch
    {
        "DNS" => "name resolution",
        "Connect" => "the TCP connection",
        "TLS" => "the TLS handshake",
        _ => "the wait for the response",
    };

    internal static byte[] BuildRequest(string method, Uri url)
    {
        string host = url.HostNameType == UriHostNameType.IPv6 ? url.Host : url.IdnHost;
        if (!url.IsDefaultPort)
        {
            host += ":" + url.Port.ToString(CultureInfo.InvariantCulture);
        }

        string version = typeof(HttpProbeTool).Assembly.GetName().Version?.ToString(3) ?? "2";
        var text = new StringBuilder(256)
            .Append(method).Append(' ').Append(url.PathAndQuery).Append(" HTTP/1.1\r\n")
            .Append("Host: ").Append(host).Append("\r\n")
            .Append("User-Agent: WinNetworkManager/").Append(version).Append(" (connection diagnostics)\r\n")
            .Append("Accept: */*\r\n")
            .Append("Connection: close\r\n\r\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }

    private static async Task<(byte[] Header, long FirstByteTimestamp)> ReadHeaderAsync(Stream stream, CancellationToken cancellationToken)
    {
        var data = new MemoryStream();
        var chunk = new byte[8192];
        long firstByte = 0;
        while (true)
        {
            int read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new DiagnosticsException(data.Length == 0
                    ? "The server closed the connection without answering."
                    : "The server closed the connection before the end of the response header.");
            }

            if (data.Length == 0)
            {
                firstByte = Stopwatch.GetTimestamp();
            }

            int searchFrom = (int)Math.Max(0, data.Length - 3);
            data.Write(chunk, 0, read);
            var buffer = data.GetBuffer().AsSpan(0, (int)data.Length);
            int end = buffer[searchFrom..].IndexOf("\r\n\r\n"u8);
            if (end >= 0)
            {
                return (buffer[..(searchFrom + end)].ToArray(), firstByte);
            }

            if (data.Length > MaxHeaderBytes)
            {
                throw new DiagnosticsException("The response header is larger than 64 KB.");
            }
        }
    }

    internal static (string Version, int Status, string Reason, IReadOnlyList<HttpHeader> Headers) ParseHeader(ReadOnlySpan<byte> header)
    {
        string text = Encoding.Latin1.GetString(header);
        string[] lines = text.Split("\r\n");
        string[] statusLine = lines[0].Split(' ', 3);
        if (statusLine.Length < 2 || !statusLine[0].StartsWith("HTTP/", StringComparison.Ordinal) ||
            !int.TryParse(statusLine[1], NumberStyles.None, CultureInfo.InvariantCulture, out int status) || status is < 100 or > 999)
        {
            throw new DiagnosticsException("The server did not answer with HTTP (is the port or the scheme right?).");
        }

        var headers = new List<HttpHeader>(lines.Length - 1);
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i];
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                headers.Add(new HttpHeader(line[..colon].Trim(), line[(colon + 1)..].Trim()));
            }
            else if (headers.Count > 0 && line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
            {
                var last = headers[^1];
                headers[^1] = last with { Value = last.Value + " " + line.Trim() }; // obsolete line folding
            }
        }

        return (statusLine[0], status, statusLine.Length > 2 ? statusLine[2] : string.Empty, headers);
    }
}
