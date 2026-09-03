namespace EmbodySense.Core.Startup.Loops.Execution.Reconciliation.Models;

/// <summary>Identifies one exact immutable reconciliation case version without exposing its execution binding.</summary>
/// <param name="CaseId">The stable bounded case identity.</param>
/// <param name="CaseVersion">The exact positive immutable version.</param>
/// <param name="ContentHash">The exact immutable case content hash.</param>
/// <param name="BindingHash">The redacted exact execution-binding hash.</param>
public sealed record GovernedLoopEffectReconciliationCaseReference(string CaseId, long CaseVersion, string ContentHash, string BindingHash)
{

    /// <summary>Gets the stable case identity.</summary>
    public string CaseId { get; } = GovernedLoopEffectReconciliationSurfaceGuard.Identifier(CaseId, nameof(CaseId));

    /// <summary>Gets the positive immutable case version.</summary>
    public long CaseVersion { get; } = CaseVersion > 0 ? CaseVersion : throw new ArgumentOutOfRangeException(nameof(CaseVersion));

    /// <summary>Gets the exact case content hash.</summary>
    public string ContentHash { get; } = GovernedLoopEffectReconciliationSurfaceGuard.Hash(ContentHash, nameof(ContentHash));

    /// <summary>Gets the redacted exact execution-binding hash.</summary>
    public string BindingHash { get; } = GovernedLoopEffectReconciliationSurfaceGuard.Hash(BindingHash, nameof(BindingHash));
}
