using System.Diagnostics;
using WinNetworkManager.Core.Processes;
using WinNetworkManager.Platform.Windows.Processes;

namespace WinNetworkManager.Platform.Tests;

public sealed class ProcessTerminatorTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public void Ends_the_instance_whose_creation_time_matches()
    {
        using var child = StartChild();
        var key = KeyOf(child);

        var result = ProcessTerminator.Terminate(key, Wait);

        Assert.Equal(TerminateOutcome.Terminated, result.Outcome);
        Assert.True(child.WaitForExit(1000));
        Assert.Equal((int)ProcessTerminator.ExitCode, child.ExitCode);
    }

    [Fact]
    public void A_different_creation_time_is_treated_as_a_reused_pid_and_nothing_is_ended()
    {
        using var child = StartChild();
        try
        {
            var key = KeyOf(child);
            var stale = new ProcessKey(key.Pid, key.CreateTimeTicks - TimeSpan.TicksPerSecond);

            var result = ProcessTerminator.Terminate(stale, Wait);

            Assert.Equal(TerminateOutcome.Replaced, result.Outcome);
            Assert.False(child.HasExited);
        }
        finally
        {
            child.Kill();
        }
    }

    [Fact]
    public void An_exited_process_is_reported_as_already_exited()
    {
        using var child = StartChild();
        var key = KeyOf(child);
        child.Kill();
        child.WaitForExit();

        var result = ProcessTerminator.Terminate(key, Wait);

        // Depending on timing Windows reports an invalid PID or a signalled handle; a reused PID would be "Replaced".
        Assert.Contains(result.Outcome, new[] { TerminateOutcome.AlreadyExited, TerminateOutcome.Replaced });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void Kernel_processes_are_refused(int pid) =>
        Assert.Equal(TerminateOutcome.Refused, ProcessTerminator.Terminate(new ProcessKey(pid, 1), Wait).Outcome);

    [Fact]
    public void WinNetworkManager_itself_is_refused()
    {
        var (created, _) = ProcessSnapshotReader.ReadCreateTime(Environment.ProcessId);
        Assert.Equal(TerminateOutcome.Refused, ProcessTerminator.Terminate(new ProcessKey(Environment.ProcessId, created), Wait).Outcome);
    }

    [Fact]
    public void Keys_without_a_creation_time_are_refused_because_the_identity_cannot_be_verified()
    {
        using var child = StartChild();
        try
        {
            var result = ProcessTerminator.Terminate(new ProcessKey(child.Id, 0), Wait);
            Assert.Equal(TerminateOutcome.Refused, result.Outcome);
            Assert.False(child.HasExited);
        }
        finally
        {
            child.Kill();
        }
    }

    // cmd.exe waiting on a redirected, never-closed stdin: a single long-lived process with no children to orphan.
    private static Process StartChild() =>
        Process.Start(new ProcessStartInfo("cmd.exe", "/q /k") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardInput = true })!;

    private static ProcessKey KeyOf(Process process)
    {
        var (created, status) = ProcessSnapshotReader.ReadCreateTime(process.Id);
        Assert.Equal(InfoStatus.Available, status);
        return new ProcessKey(process.Id, created);
    }
}
