using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom;

namespace EmbodySense.Core.Application.Loops.Models;

/// <summary>
/// Represents a custom loop definition mutation request.
/// </summary>
/// <param name="Kind">The kind.</param>
/// <param name="OperationId">The operation ID.</param>
/// <param name="RequestHash">The request hash.</param>
/// <param name="LoopId">The owning loop identifier.</param>
/// <param name="RoleId">The workspace role identifier.</param>
/// <param name="ExpectedDefinitionVersion">The expected definition version.</param>
/// <param name="PlannedDefinition">The planned definition.</param>
/// <param name="PriorDefinition">The prior definition.</param>
/// <param name="RequestedAtUtc">The requested at UTC.</param>
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
