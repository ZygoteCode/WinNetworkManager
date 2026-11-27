using System.Security.Cryptography;
using WinNetworkManager.Core.Errors;

namespace WinNetworkManager.Diagnostics;

/// <summary>Hash algorithms offered by the file hash checker, in display order.</summary>
public enum FileHashAlgorithm
{
    /// <summary>SHA-256 (64 hex digits): the usual choice for download checksums.</summary>
    Sha256,

    /// <summary>SHA-512 (128 hex digits).</summary>
    Sha512,

    /// <summary>SHA-1 (40 hex digits). Broken for security; kept for old published checksums.</summary>
    Sha1,

    /// <summary>MD5 (32 hex digits). Broken for security; kept for old published checksums.</summary>
    Md5,
}

/// <summary>One computed digest.</summary>
/// <param name="Algorithm">Algorithm.</param>
/// <param name="Hex">Digest as lowercase hexadecimal.</param>
public sealed record FileHashValue(FileHashAlgorithm Algorithm, string Hex)
{
    /// <summary>Display name ("SHA-256").</summary>
    public string Name => FileHashTool.DisplayName(Algorithm);

    /// <summary>True for algorithms that must not be trusted against deliberate tampering.</summary>
    public bool IsWeak => Algorithm is FileHashAlgorithm.Sha1 or FileHashAlgorithm.Md5;
}

/// <summary>Result of hashing one file.</summary>
/// <param name="Path">Full path of the file.</param>
/// <param name="Length">Bytes read.</param>
/// <param name="Hashes">Digests, in the order requested.</param>
/// <param name="ElapsedMs">Time spent reading and hashing.</param>
public sealed record FileHashResult(string Path, long Length, IReadOnlyList<FileHashValue> Hashes, double ElapsedMs);

/// <summary>Outcome of comparing an expected checksum with computed digests.</summary>
public enum HashMatchKind
{
    /// <summary>Nothing to compare (the expected text is empty).</summary>
    Empty,

    /// <summary>The expected text is not a hexadecimal digest of a supported length.</summary>
    Invalid,

    /// <summary>The digest of the matching length was not computed.</summary>
    NotComputed,

    /// <summary>The expected digest equals the computed one.</summary>
    Match,

    /// <summary>The expected digest differs from the computed one: the file is not the published file.</summary>
    Mismatch,
}

/// <summary>Comparison outcome.</summary>
/// <param name="Kind">Outcome.</param>
/// <param name="Algorithm">Algorithm identified from the digest length, when valid.</param>
public sealed record HashMatch(HashMatchKind Kind, FileHashAlgorithm? Algorithm);

/// <summary>
/// Computes file checksums (SHA-256, SHA-512, SHA-1, MD5) in a single read and compares them with a published value.
/// Everything happens locally: nothing is uploaded or looked up online.
/// </summary>
public static class FileHashTool
{
    private const int BufferSize = 1 << 20;

    /// <summary>All algorithms, in display order.</summary>
    public static IReadOnlyList<FileHashAlgorithm> All { get; } = Enum.GetValues<FileHashAlgorithm>();

    /// <summary>Display name of an algorithm.</summary>
    public static string DisplayName(FileHashAlgorithm algorithm) => algorithm switch
    {
        FileHashAlgorithm.Sha256 => "SHA-256",
        FileHashAlgorithm.Sha512 => "SHA-512",
        FileHashAlgorithm.Sha1 => "SHA-1",
        FileHashAlgorithm.Md5 => "MD5",
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
    };

    /// <summary>
    /// Hashes <paramref name="path"/> with every algorithm in <paramref name="algorithms"/>, reading the file once.
    /// The file is opened with full sharing so executables that are running can be hashed.
    /// </summary>
    /// <exception cref="DiagnosticsException">The file cannot be read.</exception>
    public static async Task<FileHashResult> ComputeAsync(string path, IReadOnlyList<FileHashAlgorithm> algorithms, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(algorithms);
        if (algorithms.Count == 0)
        {
            throw new ArgumentException("At least one algorithm is required.", nameof(algorithms));
        }

        var hashers = algorithms.Distinct().Select(a => (Algorithm: a, Hash: Create(a))).ToList();
        try
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            long total = 0;
            string fullPath;
            try
            {
                fullPath = System.IO.Path.GetFullPath(path.Trim().Trim('"'));
                await using var stream = new FileStream(fullPath, new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.ReadWrite | FileShare.Delete,
                    BufferSize = 0,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                });
                long length = stream.Length;
                byte[] buffer = new byte[BufferSize];
                int lastPercent = -1;
                int read;
                while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    foreach (var (_, hash) in hashers)
                    {
                        hash.AppendData(buffer, 0, read);
                    }

                    total += read;
                    if (progress is not null && length > 0)
                    {
                        int percent = (int)(total * 100 / Math.Max(length, total));
                        if (percent != lastPercent)
                        {
                            lastPercent = percent;
                            progress.Report(percent / 100.0);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                throw new DiagnosticsException(DescribeReadError(path, ex), ex);
            }

            var values = hashers.Select(h => new FileHashValue(h.Algorithm, Convert.ToHexStringLower(h.Hash.GetHashAndReset()))).ToList();
            return new FileHashResult(fullPath, total, values, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        finally
        {
            foreach (var (_, hash) in hashers)
            {
                hash.Dispose();
            }
        }
    }

    /// <summary>
    /// Extracts a digest from text as published on download pages: surrounding spaces, upper case, separators
    /// (spaces, colons, dashes), an "algorithm:" or "algorithm=" prefix and the "digest  file name" format of
    /// sha256sum are accepted. Returns null when no hexadecimal digest of a supported length is found.
    /// </summary>
    public static string? NormalizeExpected(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string s = text.Trim();
        int prefix = s.IndexOfAny([':', '=']);
        if (prefix is > 0 and <= 8 && s[..prefix].Trim().ToUpperInvariant().Replace("-", string.Empty, StringComparison.Ordinal) is "SHA256" or "SHA512" or "SHA1" or "MD5")
        {
            s = s[(prefix + 1)..].Trim();
        }

        // "digest  name" / "digest *name": keep the first token when it is a digest on its own.
        int space = s.IndexOfAny([' ', '\t']);
        if (space > 0 && IsHexOfSupportedLength(s[..space]))
        {
            s = s[..space];
        }

        var digits = new System.Text.StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (char.IsAsciiHexDigit(c))
            {
                digits.Append(char.ToLowerInvariant(c));
            }
            else if (c is not (' ' or ':' or '-' or '\t'))
            {
                return null;
            }
        }

        string hex = digits.ToString();
        return AlgorithmForLength(hex.Length) is null ? null : hex;
    }

    /// <summary>Algorithm whose hexadecimal digest has <paramref name="hexLength"/> characters.</summary>
    public static FileHashAlgorithm? AlgorithmForLength(int hexLength) => hexLength switch
    {
        64 => FileHashAlgorithm.Sha256,
        128 => FileHashAlgorithm.Sha512,
        40 => FileHashAlgorithm.Sha1,
        32 => FileHashAlgorithm.Md5,
        _ => null,
    };

    /// <summary>Compares the expected text with the computed digests (constant time per digest).</summary>
    public static HashMatch Compare(string? expected, IReadOnlyList<FileHashValue> computed)
    {
        ArgumentNullException.ThrowIfNull(computed);
        if (string.IsNullOrWhiteSpace(expected))
        {
            return new HashMatch(HashMatchKind.Empty, null);
        }

        string? hex = NormalizeExpected(expected);
        if (hex is null)
        {
            return new HashMatch(HashMatchKind.Invalid, null);
        }

        var algorithm = AlgorithmForLength(hex.Length)!.Value;
        var value = computed.FirstOrDefault(v => v.Algorithm == algorithm);
        if (value is null)
        {
            return new HashMatch(HashMatchKind.NotComputed, algorithm);
        }

        bool equal = CryptographicOperations.FixedTimeEquals(Convert.FromHexString(hex), Convert.FromHexString(value.Hex));
        return new HashMatch(equal ? HashMatchKind.Match : HashMatchKind.Mismatch, algorithm);
    }

    private static bool IsHexOfSupportedLength(string s) => AlgorithmForLength(s.Length) is not null && s.All(char.IsAsciiHexDigit);

    private static IncrementalHash Create(FileHashAlgorithm algorithm) => algorithm switch
    {
        FileHashAlgorithm.Sha256 => IncrementalHash.CreateHash(HashAlgorithmName.SHA256),
        FileHashAlgorithm.Sha512 => IncrementalHash.CreateHash(HashAlgorithmName.SHA512),
        FileHashAlgorithm.Sha1 => IncrementalHash.CreateHash(HashAlgorithmName.SHA1),
        FileHashAlgorithm.Md5 => IncrementalHash.CreateHash(HashAlgorithmName.MD5),
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
    };

    private static string DescribeReadError(string path, Exception ex) => ex switch
    {
        FileNotFoundException => $"The file \"{path}\" does not exist.",
        DirectoryNotFoundException => $"The folder of \"{path}\" does not exist.",
        UnauthorizedAccessException => $"Access to \"{path}\" was denied. Protected system files can only be read by administrators.",
        ArgumentException or NotSupportedException => $"\"{path}\" is not a valid file path.",
        _ => $"\"{path}\" could not be read: {ErrorText.Describe(ex)}",
    };
}
