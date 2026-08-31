namespace EmbodySense.Core.Startup.HumanReview.Models;

/// <summary>Projects the current run/review runtime posture without exposing authority or binding evidence.</summary>
/// <param name="RunStatus">The enclosing run status.</param>
/// <param name="FrontierStatus">The canonical frontier status.</param>
/// <param name="LifecycleStatus">The current review lifecycle status.</param>
/// <param name="ContinuationStatus">The detached continuation posture.</param>
/// <param name="LifecycleVersion">The exact optimistic lifecycle version.</param>
/// <param name="EvidenceCount">The bounded append-only review evidence count.</param>
/// <param name="DecisionCount">The bounded accepted decision count.</param>
/// <param name="ActionCount">The bounded non-approval action count.</param>
/// <param name="UpdatedAtUtc">The trusted run update time.</param>
public sealed record HumanReviewRuntimePosture(CustomLoopRunStatus RunStatus, GovernedLoopFrontierStatus FrontierStatus, HumanReviewLifecycleStatus LifecycleStatus, HumanReviewContinuationStatus ContinuationStatus, long LifecycleVersion, int EvidenceCount, int DecisionCount, int ActionCount, DateTimeOffset UpdatedAtUtc);
