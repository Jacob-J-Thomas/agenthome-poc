namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Requests one isolated invocation of an already activated immutable artifact.</summary>
/// <param name="Manifest">The exact activated artifact manifest.</param>
/// <param name="ArtifactRoot">Legacy caller path, ignored by secure resolvers.</param>
/// <param name="InputJson">The bounded JSON input written to standard input.</param>
/// <param name="OperationId">The invocation correlation identity.</param>
/// <param name="ExpectedActivationRevision">The proved activation revision the caller intends to invoke.</param>
public sealed record CapabilityExecutableInvocation(CapabilityArtifactManifest Manifest, string ArtifactRoot, string InputJson, string OperationId, long ExpectedActivationRevision = 0);
