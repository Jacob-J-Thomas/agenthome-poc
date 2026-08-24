using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Secrets.Redaction.Models;

namespace EmbodySense.Core.Common.CommandActions.Models;

/// <summary>Retains one bounded redacted conclusive command outcome without paths, environment metadata, or secrets.</summary>
/// <param name="SchemaVersion">The schema version, which must be 1.</param>
/// <param name="EffectId">The stable effect identity.</param>
/// <param name="IdempotencyOperationId">The stable operation identity.</param>
/// <param name="EffectGeneration">The exact effect generation.</param>
/// <param name="TemplateId">The exact template identity.</param>
/// <param name="TemplateVersion">The exact template version.</param>
/// <param name="TemplateHash">The exact template hash.</param>
/// <param name="ArtifactDigest">The exact executable artifact digest.</param>
/// <param name="ActivationRevision">The exact artifact activation revision.</param>
/// <param name="InputFingerprint">The canonical typed input fingerprint.</param>
/// <param name="TargetFingerprint">The exact value-free target fingerprint.</param>
/// <param name="PreconditionEvidenceHash">The exact prepared optimistic precondition hash.</param>
/// <param name="BeforeEvidenceId">The exact preparation evidence reference bound into effect intent.</param>
/// <param name="Outcome">The closed process outcome.</param>
/// <param name="Termination">The exact terminal process-tree posture.</param>
/// <param name="ExitCode">The observed exit code when available.</param>
/// <param name="RetainedStandardOutput">The bounded redacted output excerpt or structured result.</param>
/// <param name="RetainedStandardError">The bounded redacted error excerpt.</param>
/// <param name="ObservedStandardOutputBytes">The bounded observed stdout byte count.</param>
/// <param name="ObservedStandardErrorBytes">The bounded observed stderr byte count.</param>
/// <param name="DurationMilliseconds">The bounded observed duration.</param>
/// <param name="RedactionApplied">Whether the mandatory redaction boundary was applied.</param>
/// <param name="RedactionSummary">The bounded value-free summary of scope-aware standard-stream redaction.</param>
/// <param name="RecordedAtUtc">The trusted UTC observation time.</param>
/// <param name="EvidenceId">The content-addressed outcome evidence identifier.</param>
public sealed record CommandActionOutcomeEvidence(
    int SchemaVersion,
    string EffectId,
    string IdempotencyOperationId,
    long EffectGeneration,
    string TemplateId,
    long TemplateVersion,
    string TemplateHash,
    CapabilityIntegrityDigest ArtifactDigest,
    long ActivationRevision,
    string InputFingerprint,
    string TargetFingerprint,
    string PreconditionEvidenceHash,
    string BeforeEvidenceId,
    CommandActionOutcomeKind Outcome,
    CommandActionTerminationPosture Termination,
    int? ExitCode,
    string? RetainedStandardOutput,
    string? RetainedStandardError,
    int ObservedStandardOutputBytes,
    int ObservedStandardErrorBytes,
    long DurationMilliseconds,
    bool RedactionApplied,
    RedactionSummary RedactionSummary,
    DateTimeOffset RecordedAtUtc,
    string EvidenceId);
