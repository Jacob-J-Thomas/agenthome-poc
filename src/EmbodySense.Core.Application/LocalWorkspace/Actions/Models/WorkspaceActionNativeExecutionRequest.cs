using EmbodySense.Core.Common.LocalWorkspace.Actions.Models;

namespace EmbodySense.Core.Application.LocalWorkspace.Actions.Models;

/// <summary>Supplies the trusted native host with exact semantic input and durable effect identities.</summary>
/// <param name="Input">The revalidated bounded semantic input.</param>
/// <param name="TargetFingerprint">The exact server-derived target fingerprint bound into durable intent.</param>
/// <param name="BeforeEvidenceId">The content-addressed server-owned before-evidence identifier bound into intent.</param>
/// <param name="EffectId">The stable effect identity.</param>
/// <param name="IdempotencyOperationId">The stable idempotency identity.</param>
/// <param name="EffectGeneration">The exact effect generation.</param>
public sealed record WorkspaceActionNativeExecutionRequest(
    WorkspaceActionInput Input,
    string TargetFingerprint,
    string BeforeEvidenceId,
    string EffectId,
    string IdempotencyOperationId,
    long EffectGeneration);
