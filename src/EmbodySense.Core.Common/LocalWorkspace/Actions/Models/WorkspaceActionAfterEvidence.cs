namespace EmbodySense.Core.Common.LocalWorkspace.Actions.Models;

/// <summary>Contains immutable, bounded, value-free evidence of one observed workspace action outcome.</summary>
public sealed record WorkspaceActionAfterEvidence(
    int SchemaVersion,
    string EvidenceId,
    string BeforeEvidenceId,
    string OperationId,
    string EffectId,
    string IdempotencyOperationId,
    long EffectGeneration,
    string ScopeId,
    string TargetReference,
    string TargetFingerprint,
    WorkspaceActionEntryKind EntryKind,
    string? NativeIdentityFingerprint,
    string? ContentHash,
    long ByteCount,
    long AppendedByteCount,
    long GovernedVersion,
    string? QuarantineReference,
    string? TombstoneReference,
    DateTimeOffset ObservedAtUtc,
    string ContentHashOfRecord);
