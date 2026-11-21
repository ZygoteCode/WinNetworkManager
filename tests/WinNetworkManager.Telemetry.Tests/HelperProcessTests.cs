using System.Diagnostics;
using System.IO.Pipes;
using WinNetworkManager.Platform.Windows.Security;
using WinNetworkManager.Telemetry.Ipc;

namespace WinNetworkManager.Telemetry.Tests;

/// <summary>
/// Runs the real WinNetworkManager.Collector.exe (as WinNetworkManager does after the UAC prompt) and talks to it as
/// the parent. Requires an elevated test host: the helper's manifest demands administrator rights.
/// </summary>
public sealed class HelperProcessTests
{
    private static string HelperPath => Path.Combine(AppContext.BaseDirectory, ElevatedHelperTelemetrySource.HelperFileName);

    private static Process Start(IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(HelperPath) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return Process.Start(start) ?? throw new InvalidOperationException("The helper did not start.");
    }

    [Fact]
    public async Task Helper_executable_streams_frames_to_its_parent_and_exits_on_request()
    {
        Assert.SkipUnless(PrivilegeInfo.ReadCurrent().IsElevated, "Requires an elevated test run.");
        Assert.True(File.Exists(HelperPath), HelperPath);
        var ct = TestContext.Current.CancellationToken;
        var options = new CollectorHostOptions(PipeIdentity.NewPipeName(), Environment.ProcessId, PrivilegeInfo.CurrentUserSid());
        using var helper = Start(options.ToArguments());
        try
        {
            await using var pipe = new NamedPipeClientStream(".", options.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(15_000, ct);
            Assert.Equal(helper.Id, PipeIdentity.GetServerProcessId(pipe.SafePipeHandle)); // we talk to the process we started
            var hello = await TelemetryProtocol.ReadAsync(pipe, ct);
            Assert.Equal(MessageType.Hello, hello!.Value.Type);
            Assert.Equal(helper.Id, TelemetryProtocol.DecodeHello(hello.Value.Payload).ServerPid);

            bool sawFrame = false;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                while (!sawFrame)
                {
                    var message = await TelemetryProtocol.ReadAsync(pipe, timeout.Token);
                    Assert.NotNull(message);
                    sawFrame = message.Value.Type == MessageType.Frame;
                }
            }

            await TelemetryProtocol.WriteAsync(pipe, MessageType.Shutdown, ReadOnlyMemory<byte>.Empty, ct);
            Assert.True(helper.WaitForExit(15_000), "The helper did not exit after the shutdown request.");
            Assert.Equal(CollectorExitCodes.Success, helper.ExitCode);
        }
        finally
        {
            if (!helper.HasExited)
            {
                helper.Kill();
            }
        }
    }

    [Fact]
    public void Helper_refuses_to_run_without_valid_arguments()
    {
        Assert.SkipUnless(PrivilegeInfo.ReadCurrent().IsElevated, "Requires an elevated test run.");
        using var helper = Start(["--pipe", "not-a-valid-name"]);
        Assert.True(helper.WaitForExit(10_000));
        Assert.Equal(CollectorExitCodes.InvalidArguments, helper.ExitCode);
    }
}
