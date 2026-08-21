using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EmbodySense.Core.Persistence.Inference.Profiles;

internal static class ModelProfileMetadataSourceRevisionHash
{
    private const string Domain = "embodysense.model-profile-metadata-source-revision.v1";

    internal static string Compute(string profileId, long generation, string metadataHash, string? previousSourceRevisionHash, string operationId)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("generation", generation);
            writer.WriteString("metadataHash", metadataHash);
            writer.WriteString("operationId", operationId);
            writer.WriteString("previousSourceRevisionHash", previousSourceRevisionHash);
            writer.WriteString("profileId", profileId);
            writer.WriteEndObject();
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, Encoding.UTF8.GetBytes(Domain));
        Append(hash, buffer.WrittenSpan);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        hash.AppendData(length);
        hash.AppendData(value);
    }
}
