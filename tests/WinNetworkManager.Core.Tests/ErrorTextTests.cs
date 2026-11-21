using System.ComponentModel;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using WinNetworkManager.Core.Errors;

namespace WinNetworkManager.Core.Tests;

public sealed class ErrorTextTests
{
    private sealed class FriendlyException(string message) : Exception(message), IUserFacingMessage;

    [Theory]
    [InlineData(SocketError.ConnectionRefused, "The connection was refused (socket error 10061).")]
    [InlineData(SocketError.HostUnreachable, "There is no route to the host (socket error 10065).")]
    [InlineData(SocketError.TimedOut, "The operation timed out (socket error 10060).")]
    public void Socket_errors_are_english_whatever_the_system_language(SocketError error, string expected) =>
        Assert.Equal(expected, ErrorText.Describe(new SocketException((int)error)));

    [Fact]
    public void Win32_errors_are_mapped_and_unknown_codes_stay_searchable()
    {
        Assert.Equal("Access denied (error 5).", ErrorText.Describe(new Win32Exception(5)));
        Assert.Equal("The operation was cancelled by the user (error 1223).", ErrorText.Win32(1223));
        Assert.Equal("Windows error (error 4242).", ErrorText.Win32(4242));
        Assert.Equal("The certificate chain ends in an untrusted root (0x80090325).", ErrorText.Win32(unchecked((int)0x80090325)));
        Assert.Equal("Access denied (error 5).", ErrorText.Win32(unchecked((int)0x80070005))); // Win32 HRESULT
    }

    [Fact]
    public void Wrapped_errors_use_the_inner_code()
    {
        Assert.Equal("Destination host unreachable (error 11003).", ErrorText.Describe(new PingException("x", new Win32Exception(11003))));
        Assert.StartsWith("The TLS handshake failed: the server sent", ErrorText.Describe(new AuthenticationException("x", new Win32Exception(unchecked((int)0x80090326)))), StringComparison.Ordinal);
        Assert.Equal("WMI access denied (0x80041003).", ErrorText.Describe(new COMException("localized text", unchecked((int)0x80041003))));
        Assert.Equal("There is not enough space on the disk (error 112).", ErrorText.Describe(new IOException("testo localizzato", unchecked((int)0x80070070))));
        Assert.Equal("The connection was refused (socket error 10061).", ErrorText.Describe(new AggregateException(new SocketException(10061))));
    }

    [Fact]
    public void English_runtime_and_own_messages_are_kept()
    {
        Assert.Equal("already english", ErrorText.Describe(new FriendlyException("already english")));
        Assert.Equal("File in use.", ErrorText.Describe(new IOException("File in use.", unchecked((int)0x80070020))));
        Assert.Equal("Plain message.", ErrorText.Describe(new InvalidOperationException("Plain message.")));
    }
}
