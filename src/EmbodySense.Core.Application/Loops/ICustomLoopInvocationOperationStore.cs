using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Common.Loops.Models.Custom;

namespace EmbodySense.Core.Application.Loops;

public enum CustomLoopInvocationOperationState
{
    Unknown = 0,
    Pending = 1,
    Complete = 2
}

public enum CustomLoopInvocationOutcome
{
    Unknown = 0,
    WorkspaceExecutionBusy = 1,
    Admitted = 2,
    Rejected = 3
}

public sealed record CustomLoopInvocationOperation(
    int SchemaVersion,
    string OperationId,
    string RequestHash,
    string LoopId,
    int ExpectedDefinitionVersion,
    string ExpectedDefinitionHash,
    string Actor,
    string Surface,
    string CurrentRoleId,
    string InvocationPromptHash,
    string Provider,
    string? Model,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    CustomLoopInvocationOperationState State,
    CustomLoopInvocationOutcome Outcome,
    string AdmissionStatus,
    string? RunId,
    string Detail)
{
    public const int CurrentSchemaVersion = 1;
}

public enum CustomLoopInvocationOperationStoreStatus
{
    Unknown = 0,
    Created = 1,
    Replayed = 2,
    Conflict = 3,
    Completed = 4,
    NotFound = 5
}

public sealed record CustomLoopInvocationOperationStoreResult(CustomLoopInvocationOperationStoreStatus Status, CustomLoopInvocationOperation? Operation);

public interface ICustomLoopInvocationOperationStore
{
    Task<CustomLoopInvocationOperationStoreResult> BeginAsync(CustomLoopInvocationOperation operation, CancellationToken cancellationToken = default);

    Task<CustomLoopInvocationOperation?> GetAsync(string operationId, CancellationToken cancellationToken = default);

    Task<CustomLoopInvocationOperationStoreResult> CompleteAsync(CustomLoopInvocationOperation operation, CancellationToken cancellationToken = default);
}

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
