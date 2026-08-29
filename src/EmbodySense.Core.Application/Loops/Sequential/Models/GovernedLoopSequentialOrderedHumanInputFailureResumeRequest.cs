using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Loops.Sequential.Models;

/// <summary>Re-enters canonical ordered execution after one exact Human Input no-response failure was durably routed.</summary>
/// <param name="SchemaVersion">The request schema version, which must be 1.</param>
/// <param name="Anchor">The immutable admitted execution anchor.</param>
/// <param name="Plan">The deterministic plan pinned by the run.</param>
/// <param name="Artifact">The immutable graph artifact pinned by the run.</param>
/// <param name="CheckpointId">The exact retired Human Input checkpoint.</param>
/// <param name="RetirementEvidenceHash">The exact terminal checkpoint evidence hash.</param>
/// <param name="RetirementEventId">The exact classified no-response event retained in the run trace.</param>
/// <param name="FailureEvidenceHash">The exact failure-classification evidence retained by the frontier outcome.</param>
/// <param name="Actor">The bounded actor retained by the original run admission.</param>
public sealed record GovernedLoopSequentialOrderedHumanInputFailureResumeRequest(
    int SchemaVersion,
    GovernedLoopSequentialRunAnchor Anchor,
    GovernedLoopSequentialPlan Plan,
    GovernedLoopGraphRevisionArtifact Artifact,
    string CheckpointId,
    string RetirementEvidenceHash,
    string RetirementEventId,
    string FailureEvidenceHash,
    string Actor)
{
    /// <summary>Gets the only supported schema version.</summary>
    public const int CurrentSchemaVersion = 1;
}
