namespace EmbodySense.Core.Common.LocalWorkspace.Actions.Models;

/// <summary>Contains immutable value-free evidence that one exact workspace after-state was observed as an actuator outcome.</summary>
public sealed record WorkspaceActionOutcomeEvidence(
    int SchemaVersion,
    string EvidenceId,
    string BeforeEvidenceId,
    string AfterEvidenceId,
    string AfterEvidenceHash,
    string OperationId,
    string EffectId,
    string IdempotencyOperationId,
    long EffectGeneration,
    string TargetFingerprint,
    long GovernedVersion,
    string? TombstoneReference,
    DateTimeOffset ObservedAtUtc,
    string ContentHashOfRecord);
