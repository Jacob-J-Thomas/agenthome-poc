namespace EmbodySense.Core.Persistence.Capabilities.Models;

/// <summary>Represents the server-owned monotonic activation anchor for one physical workspace.</summary>
/// <param name="CurrentRevision">The only revision eligible to be current.</param>
/// <param name="CurrentContentDigest">The authenticated current content digest.</param>
/// <param name="PreviousRevision">The optional immediately previous recovery revision.</param>
/// <param name="PreviousContentDigest">The optional immediately previous recovery digest.</param>
public sealed record CapabilityArtifactTrustState(long CurrentRevision, string CurrentContentDigest, long? PreviousRevision, string? PreviousContentDigest);
