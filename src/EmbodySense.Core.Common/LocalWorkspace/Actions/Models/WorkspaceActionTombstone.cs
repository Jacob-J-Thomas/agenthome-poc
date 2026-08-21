namespace EmbodySense.Core.Common.LocalWorkspace.Actions.Models;

/// <summary>Contains immutable value-free evidence for one recoverable delete payload.</summary>
public sealed record WorkspaceActionTombstone(
    int SchemaVersion,
    string TombstoneReference,
    string BeforeEvidenceId,
    string ScopeId,
    string TargetReference,
    string TargetFingerprint,
    string NativeIdentityFingerprint,
    string ContentHash,
    long ByteCount,
    string QuarantineReference,
    string EffectId,
    string IdempotencyOperationId,
    long EffectGeneration,
    long GovernedVersion,
    DateTimeOffset QuarantinedAtUtc,
    DateTimeOffset RetainUntilUtc,
    string ContentHashOfRecord);
