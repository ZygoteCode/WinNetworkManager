using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace WinNetworkManager.Diagnostics.Tests;

public sealed class DnsWireTests
{
    [Fact]
    public void Query_has_header_question_and_edns_opt_record()
    {
        byte[] query = DnsWire.EncodeQuery(0x1234, "www.example.com", DnsRecordType.A);
        byte[] expected =
        [
            0x12, 0x34, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01,
            3, (byte)'w', (byte)'w', (byte)'w', 7, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e', 3, (byte)'c', (byte)'o', (byte)'m', 0,
            0x00, 0x01, 0x00, 0x01,
            0x00, 0x00, 0x29, 0x04, 0xD0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        ];
        Assert.Equal(expected, query);
    }

    [Theory]
    [InlineData("Example.COM.", "example.com")]
    [InlineData("bücher.example", "xn--bcher-kva.example")]
    [InlineData("_sip._tcp.example.com", "_sip._tcp.example.com")]
    [InlineData(".", "")]
    public void Names_are_normalized_to_ascii(string input, string expected) => Assert.Equal(expected, DnsWire.ToAsciiName(input));

    [Theory]
    [InlineData("exa mple.com")]
    [InlineData("a..b")]
    [InlineData("bad!name")]
    public void Invalid_names_are_rejected(string input) => Assert.Throws<DiagnosticsException>(() => DnsWire.ToAsciiName(input));

    [Fact]
    public void Label_longer_than_63_is_rejected() => Assert.Throws<DiagnosticsException>(() => DnsWire.ToAsciiName(new string('a', 64) + ".com"));

    [Fact]
    public void Decodes_all_supported_record_types_with_compression()
    {
        var b = new DnsMessageBuilder()
            .Header(0xBEEF, 0x8180, 1, 8, 1, 1)
            .Name("www.example.com").U16(1).U16(1);                                   // question at offset 12; "example" at 16
        b.Record(x => x.Pointer(12), 5, 300, x => x.Pointer(16))                     // CNAME → example.com
         .Record(x => x.Pointer(16), 1, 60, x => x.Bytes(192, 0, 2, 10))
         .Record(x => x.Pointer(16), 28, 60, x => x.Bytes(IPAddress.Parse("2001:db8::10").GetAddressBytes()))
         .Record(x => x.Pointer(16), 15, 3600, x => x.U16(10).Labels("mail").Pointer(16))
         .Record(x => x.Pointer(16), 16, 60, x => x.U8(5).Ascii("hello").U8(8).Ascii("say \"hi\""))
         .Record(x => x.Pointer(16), 33, 60, x => x.U16(10).U16(60).U16(5060).Name("sip.example.com"))
         .Record(x => x.Pointer(16), 257, 60, x => x.U8(0).U8(5).Ascii("issue").Ascii("letsencrypt.org"))
         .Record(x => x.Pointer(16), 65, 60, x => x.Bytes(0, 1, 0))
         .Record(x => x.Pointer(16), 6, 900, x => x.Name("ns1.example.com").Name("hostmaster.example.com").U32(2026112101).U32(7200).U32(3600).U32(1209600).U32(300))
         .U8(0).U16(41).U16(1232).U32(0).U16(0);                                      // OPT

        var message = DnsWire.Decode(b.ToArray());

        Assert.Equal(0xBEEF, message.Id);
        Assert.True(message.IsResponse);
        Assert.True(message.RecursionAvailable);
        Assert.Equal(DnsResponseCode.NoError, message.ResponseCode);
        Assert.Equal("www.example.com", message.QuestionName);
        Assert.Equal(1232, message.EdnsPayloadSize);
        string[] data = [.. message.Records.Select(r => $"{r.Section} {r.Name} {r.TypeText} {r.Data}")];
        Assert.Equal(
        [
            "Answer www.example.com CNAME example.com",
            "Answer example.com A 192.0.2.10",
            "Answer example.com AAAA 2001:db8::10",
            "Answer example.com MX 10 mail.example.com",
            "Answer example.com TXT \"hello\" \"say \\\"hi\\\"\"",
            "Answer example.com SRV 10 60 5060 sip.example.com",
            "Answer example.com CAA 0 issue \"letsencrypt.org\"",
            "Answer example.com TYPE65 \\# 3 000100",
            "Authority example.com SOA ns1.example.com hostmaster.example.com 2026112101 7200 3600 1209600 300",
        ], data);
        Assert.Equal(300u, message.Records[0].TimeToLive);
    }

    [Fact]
    public void Extended_rcode_from_opt_is_combined()
    {
        var b = new DnsMessageBuilder().Header(1, 0x8000, 0, 0, 0, 1).U8(0).U16(41).U16(1232).U32(0x01000000).U16(0);
        Assert.Equal(DnsResponseCode.BadVersion, DnsWire.Decode(b.ToArray()).ResponseCode);
    }

    [Fact]
    public void Malformed_messages_throw_instead_of_crashing()
    {
        Assert.Throws<DiagnosticsException>(() => DnsWire.Decode(new byte[5]));

        var loop = new DnsMessageBuilder().Header(1, 0x8180, 1, 0, 0, 0).Pointer(12).U16(1).U16(1);
        Assert.Throws<DiagnosticsException>(() => DnsWire.Decode(loop.ToArray()));

        var counts = new DnsMessageBuilder().Header(1, 0x8180, 0, 60000, 0, 0);
        Assert.Throws<DiagnosticsException>(() => DnsWire.Decode(counts.ToArray()));

        var badA = new DnsMessageBuilder().Header(1, 0x8180, 0, 1, 0, 0).Record(x => x.Name("a.example"), 1, 1, x => x.Bytes(1, 2, 3, 4, 5));
        Assert.Throws<DiagnosticsException>(() => DnsWire.Decode(badA.ToArray()));

        var truncated = new DnsMessageBuilder().Header(1, 0x8180, 1, 0, 0, 0).U8(7).Ascii("exa");
        Assert.Throws<DiagnosticsException>(() => DnsWire.Decode(truncated.ToArray()));
    }

    [Fact]
    public void Random_garbage_never_escapes_as_another_exception()
    {
        var random = new Random(42);
        for (int i = 0; i < 2000; i++)
        {
            var bytes = new byte[random.Next(0, 300)];
            random.NextBytes(bytes);
            try
            {
                DnsWire.Decode(bytes);
            }
            catch (DiagnosticsException)
            {
                // Expected for most inputs.
            }
        }
    }

    [Fact]
    public void Answers_with_wrong_id_question_or_direction_are_ignored()
    {
        byte[] query = DnsWire.EncodeQuery(0x4242, "a.example", DnsRecordType.A);
        byte[] answer = DnsMessageBuilder.AnswerTo(query, 0x8180, "192.0.2.1");
        Assert.NotNull(DnsTool.TryAccept(answer, 0x4242, "a.example", DnsRecordType.A));
        Assert.Null(DnsTool.TryAccept(answer, 0x4243, "a.example", DnsRecordType.A));
        Assert.Null(DnsTool.TryAccept(answer, 0x4242, "b.example", DnsRecordType.A));
        Assert.Null(DnsTool.TryAccept(answer, 0x4242, "a.example", DnsRecordType.AAAA));
        Assert.Null(DnsTool.TryAccept(query, 0x4242, "a.example", DnsRecordType.A)); // QR = 0
    }
}

public sealed class DnsToolTests
{
    [Theory]
    [InlineData("192.0.2.1", "1.2.0.192.in-addr.arpa")]
    [InlineData("::ffff:192.0.2.1", "1.2.0.192.in-addr.arpa")]
    [InlineData("2001:db8::1", "1.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.8.b.d.0.1.0.0.2.ip6.arpa")]
    public void Reverse_names(string address, string expected) => Assert.Equal(expected, DnsTool.ReverseName(IPAddress.Parse(address)));

    [Fact]
    public async Task Udp_query_returns_answer_and_ignores_spoofed_datagrams()
    {
        var ct = TestContext.Current.CancellationToken;
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        var serverTask = Task.Run(async () =>
        {
            var request = await server.ReceiveAsync(ct);
            byte[] query = request.Buffer;
            Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(10))); // EDNS OPT present
            byte[] spoofed = DnsMessageBuilder.AnswerTo(query, 0x8180, "203.0.113.66");
            spoofed[0] ^= 0xFF;
            await server.SendAsync(spoofed, request.RemoteEndPoint, ct);
            await server.SendAsync(DnsMessageBuilder.AnswerTo(query, 0x8180, "192.0.2.7"), request.RemoteEndPoint, ct);
        }, ct);

        var result = await DnsTool.QueryAsync("test.example", DnsRecordType.A, IPAddress.Loopback, TimeSpan.FromSeconds(5), ct, port);
        await serverTask;

        Assert.Equal("UDP", result.Transport);
        Assert.Equal(DnsResponseCode.NoError, result.ResponseCode);
        Assert.True(result.RecursionAvailable);
        var answer = Assert.Single(result.Answers);
        Assert.Equal("192.0.2.7", answer.Data);
        Assert.Equal("test.example", answer.Name);
        Assert.True(result.ElapsedMs >= 0);
    }

    [Fact]
    public async Task Truncated_udp_answer_is_retried_over_tcp()
    {
        var ct = TestContext.Current.CancellationToken;
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        var tcp = new TcpListener(IPAddress.Loopback, port);
        tcp.Start();
        try
        {
            var udpTask = Task.Run(async () =>
            {
                var request = await udp.ReceiveAsync(ct);
                await udp.SendAsync(DnsMessageBuilder.AnswerTo(request.Buffer, 0x8380), request.RemoteEndPoint, ct); // TC set, no answers
            }, ct);
            var tcpTask = Task.Run(async () =>
            {
                using var client = await tcp.AcceptTcpClientAsync(ct);
                var stream = client.GetStream();
                var prefix = new byte[2];
                await stream.ReadExactlyAsync(prefix, ct);
                var query = new byte[BinaryPrimitives.ReadUInt16BigEndian(prefix)];
                await stream.ReadExactlyAsync(query, ct);
                byte[] answer = DnsMessageBuilder.AnswerTo(query, 0x8180, "192.0.2.1", "192.0.2.2", "192.0.2.3");
                BinaryPrimitives.WriteUInt16BigEndian(prefix, (ushort)answer.Length);
                await stream.WriteAsync(prefix, ct);
                await stream.WriteAsync(answer, ct);
            }, ct);

            var result = await DnsTool.QueryAsync("big.example", DnsRecordType.A, IPAddress.Loopback, TimeSpan.FromSeconds(5), ct, port);
            await Task.WhenAll(udpTask, tcpTask);

            Assert.Equal("TCP", result.Transport);
            Assert.Equal(3, result.Answers.Count());
        }
        finally
        {
            tcp.Stop();
        }
    }

    [Fact]
    public async Task Silent_server_times_out_with_a_clear_message()
    {
        var ct = TestContext.Current.CancellationToken;
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        var ex = await Assert.ThrowsAsync<DiagnosticsException>(() => DnsTool.QueryAsync("x.example", DnsRecordType.A, IPAddress.Loopback, TimeSpan.FromMilliseconds(300), ct, port));
        Assert.Contains("No answer", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Closed_port_is_reported_as_not_accepting_queries()
    {
        var ct = TestContext.Current.CancellationToken;
        int port;
        using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        }

        var ex = await Assert.ThrowsAsync<DiagnosticsException>(() => DnsTool.QueryAsync("x.example", DnsRecordType.A, IPAddress.Loopback, TimeSpan.FromSeconds(3), ct, port));
        Assert.True(ex.Message.Contains("not accepting", StringComparison.Ordinal) || ex.Message.Contains("No answer", StringComparison.Ordinal), ex.Message);
    }

    [Fact]
    public async Task System_resolver_resolves_localhost()
    {
        var result = await DnsTool.ResolveWithSystemAsync("localhost", TestContext.Current.CancellationToken);
        Assert.Null(result.Error);
        Assert.Contains(result.Addresses, IPAddress.IsLoopback);
    }

    [Fact]
    public async Task Dns_cache_can_be_read()
    {
        var entries = await DnsCacheReader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(entries);
        Assert.All(entries, e => Assert.False(string.IsNullOrEmpty(e.Entry) && string.IsNullOrEmpty(e.Name)));
        Assert.Equal("Name does not exist", DnsCacheReader.DescribeStatus(9003));
        Assert.Equal("Answer", DnsCacheReader.DescribeSection(1));
    }
}
