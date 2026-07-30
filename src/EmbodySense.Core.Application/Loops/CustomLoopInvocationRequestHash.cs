using EmbodySense.Core.Application.Loops.Models;
using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EmbodySense.Core.Application.Loops;

/// <summary>
/// Computes, applies, and verifies the canonical custom loop invocation request hash.
/// </summary>
public static class CustomLoopInvocationRequestHash
{
    /// <summary>
    /// Computes the custom loop invocation request hash.
    /// </summary>
    /// <param name="operationId">The operation ID.</param>
    /// <param name="loopId">The loop ID.</param>
    /// <param name="expectedDefinitionVersion">The expected definition version.</param>
    /// <param name="expectedDefinitionHash">The expected definition hash.</param>
    /// <param name="actor">The actor.</param>
    /// <param name="surface">The surface.</param>
    /// <param name="currentRoleId">The current role ID.</param>
    /// <param name="invocationPrompt">The invocation prompt.</param>
    /// <param name="provider">The provider.</param>
    /// <param name="model">The model.</param>
    /// <returns>The text value.</returns>
    public static string Compute(
        string operationId,
        string loopId,
        int expectedDefinitionVersion,
        string expectedDefinitionHash,
        string actor,
        string surface,
        string currentRoleId,
        string? invocationPrompt,
        string provider,
        string? model)
    {
        return ComputeFromPromptHash(operationId, loopId, expectedDefinitionVersion, expectedDefinitionHash, actor, surface, currentRoleId, ComputePromptHash(invocationPrompt), provider, model);
    }

    /// <summary>
    /// Computes the prompt hash for the invocation prompt.
    /// </summary>
    /// <param name="invocationPrompt">The invocation prompt.</param>
    /// <returns>The text value.</returns>
    public static string ComputePromptHash(string? invocationPrompt)
    {
        var canonical = invocationPrompt?.Normalize(NormalizationForm.FormC) ?? string.Empty;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string ComputeFromPromptHash(
        string operationId,
        string loopId,
        int expectedDefinitionVersion,
        string expectedDefinitionHash,
        string actor,
        string surface,
        string currentRoleId,
        string invocationPromptHash,
        string provider,
        string? model)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("operationId", operationId);
            writer.WriteString("loopId", loopId);
            writer.WriteNumber("expectedDefinitionVersion", expectedDefinitionVersion);
            writer.WriteString("expectedDefinitionHash", expectedDefinitionHash);
            writer.WriteString("actor", actor);
            writer.WriteString("surface", surface);
            writer.WriteString("currentRoleId", currentRoleId);
            writer.WriteString("invocationPromptHash", invocationPromptHash);
            writer.WriteString("provider", provider);
            writer.WriteString("model", model);
            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();
    }

    /// <summary>
    /// Determines whether the operation matches the expected custom loop invocation request hash.
    /// </summary>
    /// <param name="operation">The operation.</param>
    /// <returns><see langword="true"/> when matches; otherwise, <see langword="false"/>.</returns>
    public static bool Matches(CustomLoopInvocationOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var expected = Encoding.ASCII.GetBytes(ComputeFromPromptHash(
            operation.OperationId,
            operation.LoopId,
            operation.ExpectedDefinitionVersion,
            operation.ExpectedDefinitionHash,
            operation.Actor,
            operation.Surface,
            operation.CurrentRoleId,
            operation.InvocationPromptHash,
            operation.Provider,
            operation.Model));
        var actual = Encoding.ASCII.GetBytes(operation.RequestHash ?? string.Empty);
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
