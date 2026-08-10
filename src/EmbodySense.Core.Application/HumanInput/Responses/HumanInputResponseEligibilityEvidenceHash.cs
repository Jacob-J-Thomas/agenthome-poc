using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Application.HumanInput.Responses;

internal static class HumanInputResponseEligibilityEvidenceHash
{
    internal static string Compute(
        string workspaceId,
        string operationId,
        string commandHash,
        HumanInputRequestReference request,
        AuthorityActorId actorId,
        string? actorRoleId,
        string authenticationEvidenceHash,
        DateTimeOffset evaluatedAtUtc)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("workspaceId", workspaceId);
            writer.WriteString("operationId", operationId);
            writer.WriteString("commandHash", commandHash);
            writer.WritePropertyName("request");
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", request.SchemaVersion);
            writer.WriteString("requestId", request.RequestId);
            writer.WriteString("requestVersionId", request.RequestVersionId);
            writer.WriteString("requestHash", request.RequestHash);
            writer.WriteEndObject();
            writer.WriteString("actorId", actorId.Value);
            if (actorRoleId is null)
            {
                writer.WriteNull("actorRoleId");
            }
            else
            {
                writer.WriteString("actorRoleId", actorRoleId);
            }
            writer.WriteString("authenticationEvidenceHash", authenticationEvidenceHash);
            writer.WriteString("evaluatedAtUtc", evaluatedAtUtc.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();
    }
}
