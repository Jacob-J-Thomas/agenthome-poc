using EmbodySense.Core.Application.Loops.Execution.Reconciliation;

namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

/// <summary>References one exact immutable reconciliation case version without embedding evidence or payload.</summary>
/// <param name="CaseId">The stable bounded canonical case identity.</param>
/// <param name="CaseVersion">The exact positive case version.</param>
/// <param name="ContentHash">The exact immutable case content hash.</param>
/// <param name="BindingHash">The exact immutable reconciliation binding hash.</param>
public sealed record GovernedLoopEffectReconciliationCaseReference(string CaseId, long CaseVersion, string ContentHash, string BindingHash)
{
    /// <summary>Gets the validated stable canonical case identity.</summary>
    public string CaseId { get; } = GovernedLoopEffectReconciliationProjectionGuard.RequireIdentifier(CaseId, nameof(CaseId));

    /// <summary>Gets the validated exact positive case version.</summary>
    public long CaseVersion { get; } = GovernedLoopEffectReconciliationProjectionGuard.RequirePositiveVersion(CaseVersion, nameof(CaseVersion));

    /// <summary>Gets the validated exact immutable case content hash.</summary>
    public string ContentHash { get; } = GovernedLoopEffectReconciliationProjectionGuard.RequireSha256(ContentHash, nameof(ContentHash));

    /// <summary>Gets the validated exact immutable reconciliation binding hash.</summary>
    public string BindingHash { get; } = GovernedLoopEffectReconciliationProjectionGuard.RequireSha256(BindingHash, nameof(BindingHash));
}
