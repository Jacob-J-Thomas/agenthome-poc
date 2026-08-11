using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Loops.Sequential.Models;

/// <summary>Requests first-wave ordered execution under one exact admitted canonical graph hand-off.</summary>
/// <param name="SchemaVersion">The request schema version, which must be 1.</param>
/// <param name="Anchor">The guard-issued immutable admission and invocation anchor.</param>
/// <param name="Plan">The builder-issued exact linear plan.</param>
/// <param name="Artifact">The immutable graph artifact from which the plan was built.</param>
/// <param name="Actor">The authenticated actor recorded by the ordered runtime.</param>
public sealed record GovernedLoopSequentialOrderedRunRequest(
    int SchemaVersion,
    GovernedLoopSequentialRunAnchor Anchor,
    GovernedLoopSequentialPlan Plan,
    GovernedLoopGraphRevisionArtifact Artifact,
    string Actor)
{
    /// <summary>Gets the only supported experimental request schema version.</summary>
    public const int CurrentSchemaVersion = 1;
}
