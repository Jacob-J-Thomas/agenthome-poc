using EmbodySense.Core.Common.Loops.Models.Custom;

namespace EmbodySense.Core.Application.Loops.Models;

public sealed record CustomLoopDefinitionMutationRequest(
    CustomLoopDefinitionMutationKind Kind,
    string OperationId,
    string RequestHash,
    string LoopId,
    string RoleId,
    int? ExpectedDefinitionVersion,
    CustomLoopDefinition? PlannedDefinition,
    CustomLoopDefinition? PriorDefinition,
    DateTimeOffset RequestedAtUtc);
