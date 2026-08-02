using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Projects one durable artifact activation without assigning it to any loop or role.</summary>
/// <param name="CapabilityId">The capability identifier.</param>
/// <param name="ArtifactDigest">The immutable artifact digest.</param>
/// <param name="PriorArtifactDigest">The immediately prior proved artifact when available.</param>
/// <param name="Revision">The positive activation-state revision.</param>
/// <param name="ActivatedAtUtc">The trusted store timestamp.</param>
public sealed record CapabilityArtifactActivation(CapabilityId CapabilityId, CapabilityIntegrityDigest ArtifactDigest, CapabilityIntegrityDigest? PriorArtifactDigest, long Revision, DateTimeOffset ActivatedAtUtc);
