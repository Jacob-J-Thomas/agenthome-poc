using System.Security.Cryptography;
using System.Text;

namespace EmbodySense.Core.Common.Loops.Models.Custom.Execution;

public static class CustomLoopTraceContentHash
{
    public static string Compute(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    }

    public static bool Matches(string content, string contentHash)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(contentHash);
        var expected = Encoding.ASCII.GetBytes(Compute(content));
        var actual = Encoding.ASCII.GetBytes(contentHash);
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
