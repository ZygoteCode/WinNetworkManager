using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace WinNetworkManager.Diagnostics.Tests;

public sealed class HttpProbeTests
{
    private static async Task<string> ReadRequestAsync(Stream stream, CancellationToken ct)
    {
        var data = new List<byte>();
        var buffer = new byte[1024];
        while (!Encoding.ASCII.GetString([.. data]).Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            int read = await stream.ReadAsync(buffer, ct);
            if (read == 0)
            {
                break;
            }

            data.AddRange(buffer.AsSpan(0, read).ToArray());
        }

        return Encoding.ASCII.GetString([.. data]);
    }

    [Fact]
    public async Task Plain_http_reports_status_headers_and_timings()
    {
        var ct = TestContext.Current.CancellationToken;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string? request = null;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            var stream = client.GetStream();
            request = await ReadRequestAsync(stream, ct);
            await stream.WriteAsync("HTTP/1.1 204 No Content\r\nServer: test\r\nX-Folded: a\r\n b\r\n\r\n"u8.ToArray(), ct);
        }, ct);

        try
        {
            var result = await HttpProbeTool.ProbeAsync(new HttpProbeSettings { Url = new Uri($"http://127.0.0.1:{port}/status?x=1") }, ct);
            await server;

            var step = Assert.Single(result.Steps);
            Assert.True(step.Succeeded, step.Error);
            Assert.Equal(204, step.StatusCode);
            Assert.Equal("No Content", step.ReasonPhrase);
            Assert.Equal("HTTP/1.1", step.HttpVersion);
            Assert.Contains(new HttpHeader("Server", "test"), step.Headers);
            Assert.Contains(new HttpHeader("X-Folded", "a b"), step.Headers);
            Assert.Null(step.DnsMs); // address literal
            Assert.NotNull(step.ConnectMs);
            Assert.NotNull(step.WaitMs);
            Assert.Null(step.TlsMs);
            Assert.StartsWith($"HEAD /status?x=1 HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n", request, StringComparison.Ordinal);
            Assert.Contains("Connection: close", request, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Redirects_are_followed_when_asked()
    {
        var ct = TestContext.Current.CancellationToken;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            for (int i = 0; i < 2; i++)
            {
                using var client = await listener.AcceptTcpClientAsync(ct);
                var stream = client.GetStream();
                string request = await ReadRequestAsync(stream, ct);
                byte[] response = request.StartsWith("GET /start ", StringComparison.Ordinal)
                    ? "HTTP/1.1 302 Found\r\nLocation: /end\r\nContent-Length: 0\r\n\r\n"u8.ToArray()
                    : "HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok"u8.ToArray();
                await stream.WriteAsync(response, ct);
            }
        }, ct);

        try
        {
            var result = await HttpProbeTool.ProbeAsync(new HttpProbeSettings { Url = new Uri($"http://127.0.0.1:{port}/start"), Method = "GET", FollowRedirects = true }, ct);
            await server;

            Assert.Equal(2, result.Steps.Count);
            Assert.Equal(302, result.Steps[0].StatusCode);
            Assert.Equal("/end", result.Steps[0].Location);
            Assert.Equal(200, result.Final.StatusCode);
            Assert.Equal("/end", result.Final.Url.AbsolutePath);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Untrusted_certificate_is_reported_with_its_details_and_not_bypassed()
    {
        var ct = TestContext.Current.CancellationToken;
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=wnm-test.invalid", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("wnm-test.invalid");
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        using var certificate = X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        bool requestReceived = false;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            await using var ssl = new SslStream(client.GetStream());
            try
            {
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, ct);
                requestReceived = (await ReadRequestAsync(ssl, ct)).Length > 0;
            }
            catch (Exception ex) when (ex is AuthenticationException or IOException)
            {
                // The client rejects the certificate and aborts the handshake.
            }
        }, ct);

        try
        {
            var result = await HttpProbeTool.ProbeAsync(new HttpProbeSettings { Url = new Uri($"https://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(15) }, ct);
            await server;

            var step = result.Final;
            Assert.False(step.Succeeded);
            Assert.Equal("TLS", step.FailedStage);
            Assert.False(requestReceived);
            var info = Assert.IsType<CertificateInfo>(step.Certificate);
            Assert.Equal("CN=wnm-test.invalid", info.Subject);
            Assert.Contains("DNS: wnm-test.invalid", info.SubjectAlternativeNames);
            Assert.True(info.PolicyErrors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors));
            Assert.Equal(2048, info.KeySize);
            Assert.StartsWith("Invalid", info.ValidationText, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Silent_server_times_out_in_the_http_stage()
    {
        var ct = TestContext.Current.CancellationToken;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            var result = await HttpProbeTool.ProbeAsync(new HttpProbeSettings { Url = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromMilliseconds(400) }, ct);
            Assert.Equal("HTTP", result.Final.FailedStage);
            Assert.Contains("Timed out", result.Final.Error, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void Non_http_answers_are_detected()
    {
        Assert.Throws<DiagnosticsException>(() => HttpProbeTool.ParseHeader("SSH-2.0-OpenSSH_9.6"u8));
        var (version, status, reason, headers) = HttpProbeTool.ParseHeader("HTTP/1.0 404 Not Found\r\nA: 1"u8);
        Assert.Equal("HTTP/1.0", version);
        Assert.Equal(404, status);
        Assert.Equal("Not Found", reason);
        Assert.Single(headers);
    }

    [Fact]
    public async Task Unsupported_scheme_is_rejected()
    {
        await Assert.ThrowsAsync<DiagnosticsException>(() => HttpProbeTool.ProbeAsync(new HttpProbeSettings { Url = new Uri("ftp://example.com/") }, TestContext.Current.CancellationToken));
    }
}
