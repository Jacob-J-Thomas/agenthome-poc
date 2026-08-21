using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace EmbodySense.Core.Application.Inference.Profiles;

internal static class GovernedModelAttemptEvidenceHash
{
    internal static string Create(string domain, params string?[] values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, domain);
        foreach (var value in values)
        {
            Append(hash, value ?? string.Empty);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
