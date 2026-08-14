using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Loops.Sequential.Models;

/// <summary>Requests ordered continuation from an already-authorized durable resume transition under the original immutable graph hand-off.</summary>
/// <param name="SchemaVersion">The request schema version, which must be 1.</param>
/// <param name="Anchor">The original guard-issued immutable admission and invocation anchor.</param>
/// <param name="Plan">The original builder-issued exact linear plan.</param>
/// <param name="Artifact">The original immutable graph artifact from which the plan was built.</param>
/// <param name="RunningLifecycleVersion">The exact durable Running lifecycle version produced by recovery.</param>
/// <param name="ResumeOperationId">The exact durable resume-operation identity.</param>
/// <param name="Actor">The authenticated actor recorded by the ordered runtime.</param>
/// <param name="ActiveRunAlreadyRegistered">Whether the lifecycle coordinator already registered local ownership.</param>
public sealed record GovernedLoopSequentialOrderedResumeRequest(
    int SchemaVersion,
    GovernedLoopSequentialRunAnchor Anchor,
    GovernedLoopSequentialPlan Plan,
    GovernedLoopGraphRevisionArtifact Artifact,
    int RunningLifecycleVersion,
    string ResumeOperationId,
    string Actor,
    bool ActiveRunAlreadyRegistered = false)
{
    /// <summary>Gets the only supported experimental request schema version.</summary>
    public const int CurrentSchemaVersion = 1;
}
