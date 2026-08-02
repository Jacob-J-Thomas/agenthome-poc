using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Persistence.Capabilities.Models;

internal sealed record CapabilityArtifactEvidenceDocument(
    int SchemaVersion,
    string CapabilityId,
    string CapabilityVersion,
    string DescriptorJson,
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
    CapabilityDependencyManifest? Dependencies,
    string TrustStatus,
    string Verifier,
    string ManifestPolicyPin,
    string ContentDigest,
    string AuthenticationTag);
