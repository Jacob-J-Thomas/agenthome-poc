namespace EmbodySense.Core.Startup.Loops.Execution.Reconciliation.Models;

/// <summary>Returns current authenticated actor and scope authorization without exposing it through the facade.</summary>
/// <param name="Status">The closed authorization status.</param>
/// <param name="RequestHash">The exact echoed authorization request hash.</param>
/// <param name="ActorId">The current authenticated actor only for a ready result.</param>
/// <param name="ScopeId">The current authenticated scope only for a ready result.</param>
/// <param name="EvidenceHash">The server-owned current authority evidence hash only for a ready result.</param>
public sealed record GovernedLoopEffectReconciliationAuthorizationResult(
    GovernedLoopEffectReconciliationAuthorizationStatus Status,
    string RequestHash,
    string? ActorId = null,
    string? ScopeId = null,
    string? EvidenceHash = null)
{

    /// <summary>Gets the closed authorization status.</summary>
    public GovernedLoopEffectReconciliationAuthorizationStatus Status { get; } = Status != GovernedLoopEffectReconciliationAuthorizationStatus.Unknown && Enum.IsDefined(Status) ? Status : throw new ArgumentOutOfRangeException(nameof(Status));
    /// <summary>Gets the exact echoed authorization request hash.</summary>
    public string RequestHash { get; } = GovernedLoopEffectReconciliationSurfaceGuard.Hash(RequestHash, nameof(RequestHash));
    /// <summary>Gets the current authenticated actor only for a ready result.</summary>
    public string? ActorId { get; } = GovernedLoopEffectReconciliationSurfaceGuard.OptionalIdentifier(ActorId, nameof(ActorId));
    /// <summary>Gets the current authenticated scope only for a ready result.</summary>
    public string? ScopeId { get; } = GovernedLoopEffectReconciliationSurfaceGuard.OptionalIdentifier(ScopeId, nameof(ScopeId));
    /// <summary>Gets the server-owned current authority evidence hash only for a ready result.</summary>
    public string? EvidenceHash { get; } = GovernedLoopEffectReconciliationSurfaceGuard.AuthorizationEvidence(Status, ActorId, ScopeId, EvidenceHash, nameof(EvidenceHash));
}
