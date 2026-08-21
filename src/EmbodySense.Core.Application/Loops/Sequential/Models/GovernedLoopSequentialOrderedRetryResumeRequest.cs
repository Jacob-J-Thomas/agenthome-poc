using EmbodySense.Core.Common.Loops.Execution.Retry.Models;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Loops.Sequential.Models;

/// <summary>Re-enters canonical ordered execution from one exact durable retry-dispatch reservation.</summary>
/// <param name="SchemaVersion">The request schema version, which must be 1.</param>
/// <param name="Anchor">The immutable admitted run anchor.</param>
/// <param name="Plan">The deterministic canonical plan.</param>
/// <param name="Artifact">The immutable admitted graph artifact.</param>
/// <param name="RetryState">The exact durable dispatched retry state.</param>
/// <param name="Actor">The bounded audit actor.</param>
public sealed record GovernedLoopSequentialOrderedRetryResumeRequest(
    int SchemaVersion,
    GovernedLoopSequentialRunAnchor Anchor,
    GovernedLoopSequentialPlan Plan,
    GovernedLoopGraphRevisionArtifact Artifact,
    GovernedLoopRetryState RetryState,
    string Actor)
{
    /// <summary>Gets the only supported experimental request schema.</summary>
    public const int CurrentSchemaVersion = 1;
}
