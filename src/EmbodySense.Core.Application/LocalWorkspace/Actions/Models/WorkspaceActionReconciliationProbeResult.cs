namespace EmbodySense.Core.Application.LocalWorkspace.Actions.Models;

/// <summary>Returns one bounded value-free read-only reconciliation proof.</summary>
/// <param name="Posture">The conclusive or indeterminate proof posture.</param>
/// <param name="AfterEvidenceId">The exact observed after evidence when one outcome is proved.</param>
/// <param name="TombstoneReference">The exact tombstone when a recoverable delete is proved.</param>
/// <param name="OutcomeEvidenceId">The distinct exact outcome evidence when one outcome is proved.</param>
public sealed record WorkspaceActionReconciliationProbeResult(
    WorkspaceActionReconciliationPosture Posture,
    string? AfterEvidenceId,
    string? TombstoneReference,
    string? OutcomeEvidenceId = null);
