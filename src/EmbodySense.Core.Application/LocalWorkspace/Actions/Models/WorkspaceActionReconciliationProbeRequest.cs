using EmbodySense.Core.Common.LocalWorkspace.Actions.Models;

namespace EmbodySense.Core.Application.LocalWorkspace.Actions.Models;

/// <summary>Identifies one exact retained attempt for a read-only reconciliation probe.</summary>
/// <param name="Input">The exact canonical workspace input durably bound to the attempt.</param>
/// <param name="TargetFingerprint">The exact server-owned target fingerprint durably bound to the attempt.</param>
/// <param name="BeforeEvidenceId">The exact content-addressed before evidence.</param>
/// <param name="EffectId">The stable effect identity.</param>
/// <param name="IdempotencyOperationId">The stable idempotency identity.</param>
/// <param name="EffectGeneration">The exact effect generation.</param>
public sealed record WorkspaceActionReconciliationProbeRequest(
    WorkspaceActionInput Input,
    string TargetFingerprint,
    string BeforeEvidenceId,
    string EffectId,
    string IdempotencyOperationId,
    long EffectGeneration);
