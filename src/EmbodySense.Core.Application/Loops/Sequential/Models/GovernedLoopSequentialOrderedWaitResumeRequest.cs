using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Loops.Sequential.Models;

/// <summary>Requests ordered re-entry after one exact durable Waiting-to-Running continuation.</summary>
public sealed record GovernedLoopSequentialOrderedWaitResumeRequest(
    int SchemaVersion,
    GovernedLoopSequentialRunAnchor Anchor,
    GovernedLoopSequentialPlan Plan,
    GovernedLoopGraphRevisionArtifact Artifact,
    int ActivationOrdinal,
    string ContinuationEvidenceHash,
    string Actor)
{
    /// <summary>Gets the only supported experimental request schema version.</summary>
    public const int CurrentSchemaVersion = 1;
}
