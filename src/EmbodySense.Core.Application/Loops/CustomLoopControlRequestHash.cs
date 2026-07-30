using EmbodySense.Core.Application.Loops.Models;
using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops;

/// <summary>
/// Computes, applies, and verifies the canonical custom loop control request hash.
/// </summary>
public static class CustomLoopControlRequestHash
{
    /// <summary>
    /// Computes the custom loop control request hash.
    /// </summary>
    /// <param name="kind">The kind.</param>
    /// <param name="runId">The run ID.</param>
    /// <param name="expectedLifecycleVersion">The expected lifecycle version.</param>
    /// <param name="operationId">The operation ID.</param>
    /// <param name="actor">The actor.</param>
    /// <returns>The text value.</returns>
    public static string Compute(CustomLoopControlKind kind, string runId, int expectedLifecycleVersion, string operationId, string actor)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", kind.ToString().ToLowerInvariant());
            writer.WriteString("runId", runId);
            writer.WriteNumber("expectedLifecycleVersion", expectedLifecycleVersion);
            writer.WriteString("operationId", operationId);
            writer.WriteString("actor", actor);
            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();
    }

    /// <summary>
    /// Determines whether the operation matches the expected custom loop control request hash.
    /// </summary>
    /// <param name="operation">The operation.</param>
    /// <returns><see langword="true"/> when matches; otherwise, <see langword="false"/>.</returns>
    public static bool Matches(CustomLoopControlOperation operation)
    {
        var expected = Encoding.ASCII.GetBytes(Compute(operation.Kind, operation.RunId, operation.ExpectedLifecycleVersion, operation.OperationId, operation.Actor));
        var actual = Encoding.ASCII.GetBytes(operation.RequestHash ?? string.Empty);
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
