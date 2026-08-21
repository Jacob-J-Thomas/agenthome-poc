namespace EmbodySense.Core.Common.Credentials.Leases.Models;

/// <summary>Binds a credential lease to one exact canonical effect attempt and irreversible boundary.</summary>
public sealed record CredentialLeaseEffectScope(
    string NodeId,
    int NodeAttempt,
    string EffectId,
    string EffectOperationId,
    string IdempotencyOperationId,
    long EffectGeneration,
    string EffectAttemptHash,
    int BoundaryKind);
