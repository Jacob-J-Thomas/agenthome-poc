using EmbodySense.Core.Application.Loops.TraceRetention.Models;
using System.Security.Cryptography;
using System.Text.Json;

namespace EmbodySense.Core.Application.Loops.TraceRetention;

/// <summary>
/// Computes, applies, and verifies the canonical custom loop trace deletion request hash.
/// </summary>
public static class CustomLoopTraceDeletionRequestHash
{
    /// <summary>
    /// Computes the custom loop trace deletion request hash.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>The text value.</returns>
    public static string Compute(CustomLoopTraceDeletionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var canonical = new CanonicalDeletionRequest(1, request.RunId, request.ExpectedTraceHash, request.OperationId, request.Actor, request.Surface);
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical))).ToLowerInvariant();
    }

    private sealed record CanonicalDeletionRequest(int SchemaVersion, string RunId, string ExpectedTraceHash, string OperationId, string Actor, string Surface);
}
