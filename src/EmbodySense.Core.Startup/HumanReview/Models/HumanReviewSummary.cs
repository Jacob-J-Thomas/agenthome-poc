using System.Collections.Immutable;

namespace EmbodySense.Core.Startup.HumanReview.Models;

/// <summary>Projects one canonical Human Review request without exposing binding, actor, role, grant, or connection evidence.</summary>
/// <param name="RunId">The exact durable run identity.</param>
/// <param name="RequestId">The immutable review request identity.</param>
/// <param name="RequestHash">The immutable request hash.</param>
/// <param name="Purpose">The bounded review purpose.</param>
/// <param name="RequestedDecisions">The closed decisions offered by the request.</param>
/// <param name="LifecycleStatus">The current durable review lifecycle status.</param>
/// <param name="RunStatus">The enclosing run status.</param>
/// <param name="FrontierStatus">The enclosing canonical frontier posture.</param>
/// <param name="LifecycleVersion">The optimistic lifecycle version required for a decision.</param>
/// <param name="UpdatedAtUtc">The trusted lifecycle update time.</param>
/// <param name="ExpiresAtUtc">The trusted inclusive decision deadline.</param>
public sealed record HumanReviewSummary(
    string RunId,
    string RequestId,
    string RequestHash,
    HumanReviewPurpose Purpose,
    ImmutableArray<HumanReviewDecisionKind> RequestedDecisions,
    HumanReviewLifecycleStatus LifecycleStatus,
    CustomLoopRunStatus RunStatus,
    GovernedLoopFrontierStatus FrontierStatus,
    long LifecycleVersion,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset ExpiresAtUtc);
