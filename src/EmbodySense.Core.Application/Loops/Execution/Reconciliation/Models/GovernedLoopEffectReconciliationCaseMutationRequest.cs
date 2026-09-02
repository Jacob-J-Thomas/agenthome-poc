using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Application.Loops.Execution.Reconciliation;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models;

/// <summary>Requests one atomic immutable reconciliation case and optional effect-head compare-exchange for one open, assess, dispose, or accepted-resolution stage.</summary>
/// <param name="OperationId">The stable workspace-global reconciliation mutation identity.</param>
/// <param name="RequestHash">The canonical hash of the complete mutation request and purpose.</param>
/// <param name="Purpose">The bounded canonical reconciliation mutation purpose.</param>
/// <param name="ExpectedCaseVersion">The exact positive case version observed before mutation, or <see langword="null"/> only for create.</param>
/// <param name="ExpectedCaseContentHash">The exact immutable case hash observed before mutation, or <see langword="null"/> only for create.</param>
/// <param name="Binding">The exact reconciliation binding, including the current immutable effect-attempt hash.</param>
/// <param name="Replacement">The fully materialized immutable reconciliation case to store.</param>
/// <param name="ReconciledEffectSuccessor">The optional validated direct effect-attempt successor, permitted only with an accepted resolution, to commit atomically with the case.</param>
public sealed record GovernedLoopEffectReconciliationCaseMutationRequest(
    string OperationId,
    string RequestHash,
    string Purpose,
    long? ExpectedCaseVersion,
    string? ExpectedCaseContentHash,
    GovernedLoopEffectReconciliationBinding Binding,
    GovernedLoopEffectReconciliationCase Replacement,
    GovernedLoopEffectAttempt? ReconciledEffectSuccessor = null)
{
    /// <summary>Gets the validated stable workspace-global mutation identity.</summary>
    public string OperationId { get; } = GovernedLoopEffectReconciliationModelGuard.RequireIdentifier(OperationId, nameof(OperationId));

    /// <summary>Gets the validated canonical complete request hash.</summary>
    public string RequestHash { get; } = GovernedLoopEffectReconciliationModelGuard.RequireSha256(RequestHash, nameof(RequestHash));

    /// <summary>Gets the validated bounded canonical mutation purpose.</summary>
    public string Purpose { get; } = GovernedLoopEffectReconciliationModelGuard.RequireIdentifier(Purpose, nameof(Purpose));

    /// <summary>Gets the exact expected positive case version, paired with <see cref="ExpectedCaseContentHash"/>, or <see langword="null"/> for create.</summary>
    public long? ExpectedCaseVersion { get; } = GovernedLoopEffectReconciliationModelGuard.RequireExpectedCaseVersion(ExpectedCaseVersion, ExpectedCaseContentHash, Replacement, nameof(ExpectedCaseVersion));

    /// <summary>Gets the exact expected case hash, paired with <see cref="ExpectedCaseVersion"/>, or <see langword="null"/> for create.</summary>
    public string? ExpectedCaseContentHash { get; } = GovernedLoopEffectReconciliationModelGuard.RequireExpectedCaseHash(ExpectedCaseVersion, ExpectedCaseContentHash, nameof(ExpectedCaseContentHash));

    /// <summary>Gets a detached exact reconciliation binding.</summary>
    public GovernedLoopEffectReconciliationBinding Binding { get; } = GovernedLoopEffectReconciliationModelGuard.CopyMutationBinding(Binding, Replacement, ReconciledEffectSuccessor, nameof(Binding));

    /// <summary>Gets a detached immutable replacement case.</summary>
    public GovernedLoopEffectReconciliationCase Replacement { get; } = GovernedLoopEffectReconciliationModelGuard.CopyRequiredCase(Replacement, nameof(Replacement));

    /// <summary>Gets the detached optional validated direct effect-attempt successor.</summary>
    public GovernedLoopEffectAttempt? ReconciledEffectSuccessor { get; } = GovernedLoopEffectReconciliationModelGuard.CopyOptionalAttempt(ReconciledEffectSuccessor, nameof(ReconciledEffectSuccessor));
}
