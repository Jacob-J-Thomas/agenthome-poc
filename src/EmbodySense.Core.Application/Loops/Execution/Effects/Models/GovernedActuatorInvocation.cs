using EmbodySense.Core.Common.Loops.Execution.Effects.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Effects.Models;

/// <summary>Supplies one server-registered adapter with bounded structured input and exact value-free intent identities.</summary>
/// <param name="Descriptor">The exact registered operation metadata.</param>
/// <param name="EffectId">The stable effect identity.</param>
/// <param name="IdempotencyOperationId">The stable idempotency identity.</param>
/// <param name="EffectGeneration">The exact attempt generation.</param>
/// <param name="Input">The bounded canonical in-memory input.</param>
/// <param name="TargetFingerprint">The exact server-resolved target fingerprint.</param>
/// <param name="PreconditionEvidenceHash">The optional optimistic-precondition evidence hash.</param>
public sealed record GovernedActuatorInvocation(
    GovernedActuatorOperationDescriptor Descriptor,
    string EffectId,
    string IdempotencyOperationId,
    long EffectGeneration,
    GovernedActuatorInputEvidence Input,
    string TargetFingerprint,
    string? PreconditionEvidenceHash);
