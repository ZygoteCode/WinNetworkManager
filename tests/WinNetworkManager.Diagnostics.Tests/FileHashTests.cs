using System.Text;

namespace WinNetworkManager.Diagnostics.Tests;

public sealed class FileHashTests : IDisposable
{
    // Standard test vectors for the ASCII string "abc".
    private const string AbcSha256 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
    private const string AbcSha1 = "a9993e364706816aba3e25717850c26c9cd0d89d";
    private const string AbcMd5 = "900150983cd24fb0d6963f7d28e17f72";
    private const string AbcSha512 = "ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f";

    private readonly string _dir = Directory.CreateTempSubdirectory("wnm-hash-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task All_algorithms_match_known_vectors_in_one_read()
    {
        string path = Write("abc.txt", "abc");
        var result = await FileHashTool.ComputeAsync(path, FileHashTool.All, null, TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Length);
        Assert.Equal(AbcSha256, Hex(result, FileHashAlgorithm.Sha256));
        Assert.Equal(AbcSha512, Hex(result, FileHashAlgorithm.Sha512));
        Assert.Equal(AbcSha1, Hex(result, FileHashAlgorithm.Sha1));
        Assert.Equal(AbcMd5, Hex(result, FileHashAlgorithm.Md5));
    }

    [Fact]
    public async Task Large_files_spanning_several_buffers_report_progress_to_completion()
    {
        string path = Path.Combine(_dir, "big.bin");
        byte[] data = new byte[(3 << 20) + 123];
        new Random(42).NextBytes(data);
        await File.WriteAllBytesAsync(path, data, TestContext.Current.CancellationToken);
        var reports = new List<double>();

        var result = await FileHashTool.ComputeAsync(path, [FileHashAlgorithm.Sha256], new SyncProgress(reports.Add), TestContext.Current.CancellationToken);

        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data)), Hex(result, FileHashAlgorithm.Sha256));
        Assert.Equal(1.0, reports[^1]);
    }

    [Fact]
    public async Task Files_open_for_writing_by_another_program_can_be_hashed()
    {
        string path = Write("locked.txt", "abc");
        using var holder = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var result = await FileHashTool.ComputeAsync(path, [FileHashAlgorithm.Sha256], null, TestContext.Current.CancellationToken);
        Assert.Equal(AbcSha256, Hex(result, FileHashAlgorithm.Sha256));
    }

    [Fact]
    public async Task Missing_files_raise_a_user_facing_message()
    {
        var ex = await Assert.ThrowsAsync<DiagnosticsException>(() =>
            FileHashTool.ComputeAsync(Path.Combine(_dir, "missing.exe"), FileHashTool.All, null, TestContext.Current.CancellationToken));
        Assert.Contains("does not exist", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AbcSha256, AbcSha256)]
    [InlineData("  BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD  ", AbcSha256)]
    [InlineData("SHA256: " + AbcSha256, AbcSha256)]
    [InlineData("sha-256=" + AbcSha256, AbcSha256)]
    [InlineData(AbcSha256 + "  setup.exe", AbcSha256)]
    [InlineData(AbcSha256 + " *setup.exe", AbcSha256)]
    [InlineData("90:01:50:98:3c:d2:4f:b0:d6:96:3f:7d:28:e1:7f:72", AbcMd5)]
    [InlineData("a9993e36 4706816a ba3e2571 7850c26c 9cd0d89d", AbcSha1)]
    public void Published_formats_are_normalized(string input, string expected) =>
        Assert.Equal(expected, FileHashTool.NormalizeExpected(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("zz7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    [InlineData("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015a")]
    [InlineData("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015adff")]
    [InlineData("https://example.com/file.sha256")]
    public void Malformed_digests_are_rejected(string input) =>
        Assert.Null(FileHashTool.NormalizeExpected(input));

    [Fact]
    public void Compare_identifies_the_algorithm_from_the_length()
    {
        var computed = new[] { new FileHashValue(FileHashAlgorithm.Sha256, AbcSha256), new FileHashValue(FileHashAlgorithm.Md5, AbcMd5) };

        Assert.Equal(new HashMatch(HashMatchKind.Match, FileHashAlgorithm.Sha256), FileHashTool.Compare(AbcSha256.ToUpperInvariant(), computed));
        Assert.Equal(new HashMatch(HashMatchKind.Match, FileHashAlgorithm.Md5), FileHashTool.Compare(AbcMd5, computed));
        Assert.Equal(new HashMatch(HashMatchKind.Mismatch, FileHashAlgorithm.Sha256), FileHashTool.Compare(new string('0', 64), computed));
        Assert.Equal(new HashMatch(HashMatchKind.NotComputed, FileHashAlgorithm.Sha1), FileHashTool.Compare(AbcSha1, computed));
        Assert.Equal(new HashMatch(HashMatchKind.Invalid, null), FileHashTool.Compare("not a hash", computed));
        Assert.Equal(new HashMatch(HashMatchKind.Empty, null), FileHashTool.Compare(" ", computed));
    }

    private string Write(string name, string content)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private static string Hex(FileHashResult result, FileHashAlgorithm algorithm) => result.Hashes.Single(h => h.Algorithm == algorithm).Hex;

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
