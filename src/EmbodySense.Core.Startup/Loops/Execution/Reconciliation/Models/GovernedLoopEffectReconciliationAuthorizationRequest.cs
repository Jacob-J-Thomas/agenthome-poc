namespace EmbodySense.Core.Startup.Loops.Execution.Reconciliation.Models;

/// <summary>Requests current interface authorization for one exact redacted reconciliation purpose.</summary>
/// <param name="WorkspaceId">The exact server-owned workspace scope.</param>
/// <param name="SurfaceId">The exact runtime surface.</param>
/// <param name="Purpose">The distinct reconciliation purpose.</param>
/// <param name="Case">The exact immutable redacted case reference.</param>
/// <param name="RequestHash">The canonical hash binding the hidden execution binding and all public terms.</param>
public sealed record GovernedLoopEffectReconciliationAuthorizationRequest(string WorkspaceId, string SurfaceId, string Purpose, GovernedLoopEffectReconciliationCaseReference Case, string RequestHash)
{

    /// <summary>Gets the exact server-owned workspace scope.</summary>
    public string WorkspaceId { get; } = GovernedLoopEffectReconciliationSurfaceGuard.WorkspaceId(WorkspaceId, nameof(WorkspaceId));
    /// <summary>Gets the exact runtime surface.</summary>
    public string SurfaceId { get; } = GovernedLoopEffectReconciliationSurfaceGuard.Identifier(SurfaceId, nameof(SurfaceId));
    /// <summary>Gets the distinct reconciliation purpose.</summary>
    public string Purpose { get; } = GovernedLoopEffectReconciliationSurfaceGuard.Identifier(Purpose, nameof(Purpose));
    /// <summary>Gets the exact immutable redacted case reference.</summary>
    public GovernedLoopEffectReconciliationCaseReference Case { get; } = Case ?? throw new ArgumentNullException(nameof(Case));
    /// <summary>Gets the canonical hash binding the hidden execution binding and all public request terms.</summary>
    public string RequestHash { get; } = GovernedLoopEffectReconciliationSurfaceGuard.Hash(RequestHash, nameof(RequestHash));
}
