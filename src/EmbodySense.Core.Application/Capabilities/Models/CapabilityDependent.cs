using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Projects one versioned dependent while leaving its domain-owned semantics unchanged.</summary>
/// <param name="Kind">The dependent domain.</param>
/// <param name="Identity">The stable domain identity.</param>
/// <param name="Revision">The exact domain revision or canonical content hash.</param>
/// <param name="Manifest">The validated dependency manifest.</param>
/// <param name="AuthorityPosture">The non-granting authority posture.</param>
public sealed record CapabilityDependent(CapabilityDependentKind Kind, string Identity, string Revision, CapabilityDependencyManifest Manifest, CapabilityAuthorityPosture AuthorityPosture);
