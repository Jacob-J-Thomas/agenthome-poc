using System.Security.Cryptography;
using System.Text.Json;

namespace EmbodySense.Core.Application.Loops.TraceRetention;

public static class CustomLoopTraceDeletionRequestHash
{
    public static string Compute(CustomLoopTraceDeletionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var canonical = new CanonicalDeletionRequest(1, request.RunId, request.ExpectedTraceHash, request.OperationId, request.Actor, request.Surface);
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical))).ToLowerInvariant();
    }

    private sealed record CanonicalDeletionRequest(int SchemaVersion, string RunId, string ExpectedTraceHash, string OperationId, string Actor, string Surface);
}
