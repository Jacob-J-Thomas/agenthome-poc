using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using System.Security.Cryptography;
using System.Text;

namespace EmbodySense.Core.Common.Loops.Custom.Execution;

/// <summary>
/// Computes, applies, and verifies the canonical custom loop trace content hash.
/// </summary>
public static class CustomLoopTraceContentHash
{
    /// <summary>
    /// Computes the lowercase SHA-256 digest of exact UTF-8 trace content.
    /// </summary>
    /// <param name="content">The exact retained content.</param>
    /// <returns>A 64-character lowercase hexadecimal digest.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="content"/> is <see langword="null"/>.</exception>
    public static string Compute(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    }

    /// <summary>
    /// Determines whether exact UTF-8 content matches a stored digest.
    /// </summary>
    /// <param name="content">The exact retained content.</param>
    /// <param name="contentHash">The expected lowercase hexadecimal digest.</param>
    /// <returns><see langword="true"/> when stored and recomputed ASCII digests have equal length and fixed-time equality; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when either argument is <see langword="null"/>.</exception>
    public static bool Matches(string content, string contentHash)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(contentHash);
        var expected = Encoding.ASCII.GetBytes(Compute(content));
        var actual = Encoding.ASCII.GetBytes(contentHash);
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
