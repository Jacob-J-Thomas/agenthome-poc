using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Loops.Sequential.Models;

/// <summary>Re-enters canonical ordered execution only after an exact Human Input checkpoint terminal and frontier completion are durable.</summary>
/// <param name="SchemaVersion">The request schema version, which must be 1.</param>
/// <param name="Anchor">The immutable admitted execution anchor.</param>
/// <param name="Plan">The deterministic plan pinned by the run.</param>
/// <param name="Artifact">The immutable graph artifact pinned by the run.</param>
/// <param name="CheckpointId">The terminalized Human Input checkpoint identity.</param>
/// <param name="TerminalizationReceiptHash">The exact generic prepared-wake evidence hash retained by the terminal checkpoint.</param>
/// <param name="Actor">The bounded actor retained by the original run admission.</param>
public sealed record GovernedLoopSequentialOrderedHumanInputResumeRequest(
    int SchemaVersion,
    GovernedLoopSequentialRunAnchor Anchor,
    GovernedLoopSequentialPlan Plan,
    GovernedLoopGraphRevisionArtifact Artifact,
    string CheckpointId,
    string TerminalizationReceiptHash,
    string Actor)
{
    /// <summary>Gets the only supported schema version.</summary>
    public const int CurrentSchemaVersion = 1;
}
