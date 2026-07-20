using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EmbodySense.Core.Application.Loops;

public static class CustomLoopInvocationRequestHash
{
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
