using EmbodySense.Core.Application.Capabilities.Models;

namespace EmbodySense.Core.Application.Credentials.Models;

/// <summary>Projects one affected dependent without changing its domain-owned authority semantics.</summary>
public sealed record CredentialLifecycleImpact(CapabilityDependentKind Kind, string Identity, string Revision, CapabilityAuthorityPosture AuthorityPosture);
