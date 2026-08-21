using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EmbodySense.Core.Persistence.Inference.Profiles;

internal static class ModelProfileMetadataOperationReceiptHash
{
    private const string Domain = "embodysense.model-profile-metadata-operation-receipt.v1";

    internal static string Compute(string profileId, long profileGeneration, string metadataHash, string? expectedSourceRevisionHash, string publishedSourceRevisionHash, string operationId)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("expectedSourceRevisionHash", expectedSourceRevisionHash);
            writer.WriteString("metadataHash", metadataHash);
            writer.WriteString("operationId", operationId);
            writer.WriteNumber("profileGeneration", profileGeneration);
            writer.WriteString("profileId", profileId);
            writer.WriteString("publishedSourceRevisionHash", publishedSourceRevisionHash);
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
