using EmbodySense.Core.Common.Loops.Models.Custom;

namespace EmbodySense.Core.Application.Loops;

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
    CustomLoopValidationError[] ValidationErrors,
    string Detail)
{
    public const int CurrentSchemaVersion = 1;
}
