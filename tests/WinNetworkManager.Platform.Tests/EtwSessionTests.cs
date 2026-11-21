using WinNetworkManager.Platform.Windows.Etw;
using WinNetworkManager.Platform.Windows.Security;

namespace WinNetworkManager.Platform.Tests;

/// <summary>ETW controller tests. They need an elevated test host and are skipped otherwise.</summary>
public sealed class EtwSessionTests
{
    private static readonly Guid KernelNetwork = new("7DD42A49-5329-4832-8DFD-43D979153A88");

    [Fact]
    public void Session_lifecycle_start_query_flush_stop_and_stale_cleanup()
    {
        Assert.SkipUnless(PrivilegeInfo.ReadCurrent().IsElevated, "Requires an elevated test run.");
        string name = "WinNetworkManager-Test-" + Environment.ProcessId;

        var session = EtwRealTimeSession.Start(new EtwSessionOptions(name));
        try
        {
            Assert.True(session.IsRunning);
            Assert.True(session.UsesMillisecondFlushTimer);
            session.EnableProvider(KernelNetwork, 4, 0x30);
            var stats = session.QueryStatistics();
            Assert.True(stats.NumberOfBuffers > 0);
            session.Flush();

            // Simulate a crashed instance: a new Start with the same name must replace the leaked session.
            using var second = EtwRealTimeSession.Start(new EtwSessionOptions(name));
            Assert.True(second.IsRunning);
        }
        finally
        {
            session.Stop();
            EtwRealTimeSession.TryStopExisting(name);
        }

        Assert.False(EtwRealTimeSession.TryStopExisting(name), "The session must be gone after Stop().");
        session.Stop(); // idempotent
    }

    [Fact]
    public void Access_denied_is_classified()
    {
        var ex = new Windows.Interop.NativeCallException("StartTrace", 5);
        Assert.True(EtwRealTimeSession.IsAccessDenied(ex));
        Assert.False(EtwRealTimeSession.IsAccessDenied(new Windows.Interop.NativeCallException("StartTrace", 87)));
    }
}
