using EmbodySense.Core.Application.Loops.Execution.Reconciliation;

namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

/// <summary>Projects only exact immutable case identity, version, binding, and lifecycle status for bounded discovery.</summary>
/// <param name="CaseId">The stable bounded canonical case identity.</param>
/// <param name="CaseVersion">The exact positive case version.</param>
/// <param name="ContentHash">The exact immutable case content hash.</param>
/// <param name="BindingHash">The exact immutable reconciliation binding hash.</param>
/// <param name="Status">The exact closed operator-visible case posture.</param>
public sealed record GovernedLoopEffectReconciliationCaseSummary(string CaseId, long CaseVersion, string ContentHash, string BindingHash, GovernedLoopEffectReconciliationCaseSummaryStatus Status)
{
    /// <summary>Gets the validated stable canonical case identity.</summary>
    public string CaseId { get; } = GovernedLoopEffectReconciliationProjectionGuard.RequireIdentifier(CaseId, nameof(CaseId));

    /// <summary>Gets the validated exact positive case version.</summary>
    public long CaseVersion { get; } = GovernedLoopEffectReconciliationProjectionGuard.RequirePositiveVersion(CaseVersion, nameof(CaseVersion));

    /// <summary>Gets the validated exact immutable case content hash.</summary>
    public string ContentHash { get; } = GovernedLoopEffectReconciliationProjectionGuard.RequireSha256(ContentHash, nameof(ContentHash));

    /// <summary>Gets the validated exact immutable reconciliation binding hash.</summary>
    public string BindingHash { get; } = GovernedLoopEffectReconciliationProjectionGuard.RequireSha256(BindingHash, nameof(BindingHash));

    /// <summary>Gets the validated exact closed operator-visible case posture.</summary>
    public GovernedLoopEffectReconciliationCaseSummaryStatus Status { get; } = GovernedLoopEffectReconciliationProjectionGuard.RequireSummaryStatus(Status, nameof(Status));
}
