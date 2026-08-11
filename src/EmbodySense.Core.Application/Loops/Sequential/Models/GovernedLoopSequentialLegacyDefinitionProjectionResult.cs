using EmbodySense.Core.Common.Loops.Custom;

namespace EmbodySense.Core.Application.Loops.Sequential.Models;

/// <summary>Returns one deterministic compatibility definition or a fail-closed projection status.</summary>
/// <param name="Status">The closed projection status.</param>
/// <param name="Definition">The validated compatibility definition only when <paramref name="Status"/> is <see cref="GovernedLoopSequentialLegacyDefinitionProjectionStatus.Ready"/>.</param>
public sealed record GovernedLoopSequentialLegacyDefinitionProjectionResult(
    GovernedLoopSequentialLegacyDefinitionProjectionStatus Status,
    CustomLoopDefinition? Definition);
