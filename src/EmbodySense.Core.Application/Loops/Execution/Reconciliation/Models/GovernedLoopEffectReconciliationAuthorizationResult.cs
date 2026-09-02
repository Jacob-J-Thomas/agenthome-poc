using EmbodySense.Core.Common.Loops.Execution.Reconciliation;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

/// <summary>Returns current authorization bound to one exact reconciliation purpose, case, and binding.</summary>
/// <param name="Status">The closed authorization disposition.</param>
/// <param name="Purpose">The exact echoed reconciliation purpose.</param>
/// <param name="Case">The exact echoed immutable case reference.</param>
/// <param name="Binding">The exact echoed reconciliation binding.</param>
/// <param name="AuthorityEvidenceHash">The canonical server-owned authority evidence hash only when safely established.</param>
public sealed record GovernedLoopEffectReconciliationAuthorizationResult(
    GovernedLoopEffectReconciliationAuthorizationStatus Status,
    string Purpose,
    GovernedLoopEffectReconciliationCaseReference Case,
    GovernedLoopEffectReconciliationBinding Binding,
    string? AuthorityEvidenceHash)
{
    /// <summary>Gets the validated closed authorization disposition.</summary>
    public GovernedLoopEffectReconciliationAuthorizationStatus Status { get; } = GovernedLoopEffectReconciliationModelGuard.RequireDefinedStatus(Status, nameof(Status));

    /// <summary>Gets the validated exact purpose.</summary>
    public string Purpose { get; } = GovernedLoopEffectReconciliationModelGuard.CopyAuthorizationPurpose(Status, Purpose, Case, Binding, AuthorityEvidenceHash, nameof(Purpose));

    /// <summary>Gets a detached exact case reference.</summary>
    public GovernedLoopEffectReconciliationCaseReference Case { get; } = GovernedLoopEffectReconciliationModelGuard.CopyAuthorizationCase(Case, nameof(Case));

    /// <summary>Gets a detached exact reconciliation binding.</summary>
    public GovernedLoopEffectReconciliationBinding Binding { get; } = GovernedLoopEffectReconciliationModelGuard.CopyAuthorizationBinding(Case, Binding, nameof(Binding));

    /// <summary>Gets the validated canonical authority evidence hash only when ready.</summary>
    public string? AuthorityEvidenceHash { get; } = GovernedLoopEffectReconciliationModelGuard.CopyAuthorizationEvidenceHash(Status, AuthorityEvidenceHash, nameof(AuthorityEvidenceHash));
}
