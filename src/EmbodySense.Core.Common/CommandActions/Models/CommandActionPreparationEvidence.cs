using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Common.CommandActions.Models;

/// <summary>Retains value-free exact artifact and input evidence before durable effect intent.</summary>
/// <param name="SchemaVersion">The schema version, which must be 1.</param>
/// <param name="TemplateId">The exact template identity.</param>
/// <param name="TemplateVersion">The exact template version.</param>
/// <param name="TemplateHash">The exact template content hash.</param>
/// <param name="ArtifactDigest">The immutable executable artifact digest.</param>
/// <param name="ActivationRevision">The exact active artifact revision.</param>
/// <param name="InputFingerprint">The canonical typed input fingerprint without input values.</param>
/// <param name="TargetFingerprint">The exact value-free process target fingerprint.</param>
/// <param name="PreconditionEvidenceHash">The exact template, artifact, and isolation precondition hash.</param>
/// <param name="RecordedAtUtc">The trusted UTC preparation time.</param>
/// <param name="EvidenceId">The content-addressed evidence identifier.</param>
public sealed record CommandActionPreparationEvidence(
    int SchemaVersion,
    string TemplateId,
    long TemplateVersion,
    string TemplateHash,
    CapabilityIntegrityDigest ArtifactDigest,
    long ActivationRevision,
    string InputFingerprint,
    string TargetFingerprint,
    string PreconditionEvidenceHash,
    DateTimeOffset RecordedAtUtc,
    string EvidenceId);
