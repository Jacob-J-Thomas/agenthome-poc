using EmbodySense.Core.Application.Capabilities.Models;

namespace EmbodySense.Core.Persistence.Capabilities.Models;

internal sealed record CapabilityArtifactEvidenceDocument(
    int SchemaVersion,
    string CapabilityId,
    string CapabilityVersion,
    string ProviderId,
    string ImplementationId,
    string SourceKind,
    string SourceUri,
    string SourceRevision,
    string UpdatePolicy,
    string Checksum,
    CapabilityArtifactSignatureEvidence? Signature,
    string Platform,
    string EntryPoint,
    IReadOnlyList<string> Arguments,
    string TrustStatus,
    string Verifier,
    string ManifestPolicyPin,
    string ContentDigest,
    string AuthenticationTag);
