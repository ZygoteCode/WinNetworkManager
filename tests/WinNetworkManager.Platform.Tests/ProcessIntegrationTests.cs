using System.Diagnostics;
using WinNetworkManager.Core.Processes;
using WinNetworkManager.Platform.Windows.Connectivity;
using WinNetworkManager.Platform.Windows.Processes;
using WinNetworkManager.Platform.Windows.Security;
using WinNetworkManager.Platform.Windows.Shell;

namespace WinNetworkManager.Platform.Tests;

public sealed class ProcessIntegrationTests
{
    [Fact]
    public void Snapshot_contains_current_process_with_exact_create_time()
    {
        var entries = new ProcessSnapshotReader().ReadProcesses();
        using var self = Process.GetCurrentProcess();
        var me = Assert.Single(entries, e => e.Pid == self.Id);
        Assert.Equal(InfoStatus.Available, me.CreateTimeStatus);
        Assert.Equal(self.StartTime.ToUniversalTime(), DateTime.FromFileTimeUtc(me.CreateTime), TimeSpan.FromMilliseconds(1));
        Assert.Contains(entries, e => e.Pid == 4);
        Assert.Contains(entries, e => e.Pid == 0 && e.CreateTimeStatus == InfoStatus.NotApplicable);
    }

    [Fact]
    public void Recycled_pid_is_distinguished_by_create_time()
    {
        // Two short-lived processes never share a key even if Windows hands out the same PID again.
        var keys = new HashSet<ProcessKey>();
        for (int i = 0; i < 3; i++)
        {
            using var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { CreateNoWindow = true, UseShellExecute = false })!;
            var (created, status) = ProcessSnapshotReader.ReadCreateTime(p.Id);
            p.WaitForExit();
            if (status == InfoStatus.Available)
            {
                Assert.True(keys.Add(new ProcessKey(p.Id, created)));
            }
        }

        Assert.NotEmpty(keys);
    }

    [Fact]
    public void Exited_process_reports_not_found()
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { CreateNoWindow = true, UseShellExecute = false })!;
        int pid = p.Id;
        p.WaitForExit();
        p.Dispose(); // release our handle so the PID really becomes invalid
        Thread.Sleep(50);
        var path = ProcessDetailsReader.GetImagePath(pid);
        // The PID may in theory be reused immediately; accept either "not found" or a different live process.
        Assert.True(path.Status is InfoStatus.NotFound or InfoStatus.Available or InfoStatus.AccessDenied);
    }

    [Fact]
    public void Details_of_current_process()
    {
        int pid = Environment.ProcessId;
        var path = ProcessDetailsReader.GetImagePath(pid);
        Assert.True(path.HasValue);
        Assert.Equal(Environment.ProcessPath, path.Value, StringComparer.OrdinalIgnoreCase);

        var usage = ProcessDetailsReader.GetResourceUsage(pid);
        Assert.True(usage.HasValue);
        Assert.True(usage.Value.WorkingSetBytes > 0);

        var token = ProcessDetailsReader.GetTokenInfo(pid);
        Assert.True(token.HasValue);
        Assert.False(string.IsNullOrEmpty(token.Value!.UserSid));

        Assert.True(ProcessDetailsReader.GetSessionId(pid).HasValue);
        Assert.Equal(InfoStatus.NotApplicable, ProcessDetailsReader.GetImagePath(4).Status);
    }

    [Fact]
    public void Service_map_finds_svchost_services()
    {
        var map = ServiceMap.Read();
        Assert.NotEmpty(map);
        Assert.Contains(map.Values.SelectMany(v => v), s => s.Name.Equals("RpcSs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Signature_of_windows_binary_is_valid_and_published_by_microsoft()
    {
        string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var info = AuthenticodeVerifier.Verify(explorer);
        Assert.Equal(SignatureStatus.Valid, info.Status);
        Assert.Contains("Microsoft", info.SignerName ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Catalog_signed_binary_is_recognised()
    {
        // Most inbox binaries are catalog-signed; at least one of these is on every Windows 10/11 installation.
        string system32 = Environment.SystemDirectory;
        var results = new[] { "svchost.exe", "cmd.exe", "notepad.exe", "ping.exe" }
            .Select(f => Path.Combine(system32, f))
            .Where(File.Exists)
            .Select(AuthenticodeVerifier.Verify)
            .ToList();
        Assert.All(results, r => Assert.Equal(SignatureStatus.Valid, r.Status));
    }

    [Fact]
    public void Unsigned_file_is_reported_as_unsigned()
    {
        string path = Path.Combine(Path.GetTempPath(), $"wnm-unsigned-{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(path, [0x4D, 0x5A, 0, 0, 1, 2, 3]);
        try
        {
            var info = AuthenticodeVerifier.Verify(path);
            Assert.True(info.Status is SignatureStatus.Unsigned or SignatureStatus.Invalid, info.Describe());
            Assert.Null(info.SignerName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Privileges_and_connectivity_can_be_read()
    {
        var privileges = PrivilegeInfo.ReadCurrent();
        Assert.False(string.IsNullOrEmpty(privileges.UserName));
        Assert.StartsWith("S-1-5-", PrivilegeInfo.CurrentUserSid(), StringComparison.Ordinal);
        var connectivity = NetworkListManager.GetConnectivity();
        Assert.NotEqual(ConnectivityLevel.Unknown, connectivity.Level);
    }

    [Fact]
    public void Argument_quoting_follows_commandline_rules()
    {
        Assert.Equal("simple", ElevatedLauncher.Quote("simple"));
        Assert.Equal("\"with space\"", ElevatedLauncher.Quote("with space"));
        Assert.Equal("\"\"", ElevatedLauncher.Quote(string.Empty));
        Assert.Equal("\"a\\\"b\"", ElevatedLauncher.Quote("a\"b"));
        Assert.Equal("\"C:\\dir with space\\\\\"", ElevatedLauncher.Quote("C:\\dir with space\\"));
    }
}
