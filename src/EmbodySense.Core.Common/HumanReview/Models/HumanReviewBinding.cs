namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Binds one Human Review request to exact admitted run, revision, node activation or visit, attempt, frontier, authority, capability, and executable-input evidence.</summary>
/// <param name="SchemaVersion">The binding schema version, which must be 1.</param>
/// <param name="WorkspaceId">The exact canonical workspace scope.</param>
/// <param name="RunId">The exact admitted run identity.</param>
/// <param name="GraphId">The exact governed-loop graph identity.</param>
/// <param name="RevisionId">The exact immutable graph revision identity.</param>
/// <param name="RevisionHash">The exact immutable executable revision hash.</param>
/// <param name="NodeId">The exact graph node identity.</param>
/// <param name="ActivationOrdinal">The exact zero-based activation ordinal, or <see langword="null"/> only when a visit ordinal names this request.</param>
/// <param name="VisitOrdinal">The exact positive node visit ordinal, or <see langword="null"/> only when an activation ordinal names this request.</param>
/// <param name="Attempt">The exact positive node retry attempt.</param>
/// <param name="FrontierId">The exact parked frontier or checkpoint identity.</param>
/// <param name="FrontierVersion">The positive immutable frontier version that may be released.</param>
/// <param name="FrontierHash">The exact canonical parked-frontier hash.</param>
/// <param name="AuthorityProfileHash">The exact admitted authority profile hash.</param>
/// <param name="AuthorityGrantHash">The exact admitted authority grant evidence hash.</param>
/// <param name="CapabilityHash">The exact admitted capability and actuator identity hash.</param>
/// <param name="ModelProfileHash">The exact admitted inference profile hash.</param>
/// <param name="TargetHash">The exact server-resolved target hash.</param>
/// <param name="PreconditionHash">The exact optimistic-precondition evidence hash.</param>
/// <param name="PayloadHash">The exact immutable continuation or effect payload hash.</param>
/// <param name="EffectAttempt">The optional exact pre-dispatch effect-attempt binding.</param>
/// <param name="BindingHash">The canonical hash of every prior binding field.</param>
public sealed record HumanReviewBinding(
    int SchemaVersion,
    string WorkspaceId,
    string RunId,
    string GraphId,
    string RevisionId,
    string RevisionHash,
    string NodeId,
    int? ActivationOrdinal,
    int? VisitOrdinal,
    int Attempt,
    string FrontierId,
    long FrontierVersion,
    string FrontierHash,
    string AuthorityProfileHash,
    string AuthorityGrantHash,
    string CapabilityHash,
    string ModelProfileHash,
    string TargetHash,
    string PreconditionHash,
    string PayloadHash,
    HumanReviewEffectAttemptBinding? EffectAttempt,
    string BindingHash);
