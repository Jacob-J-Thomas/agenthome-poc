namespace EmbodySense.Core.Persistence.Capabilities.Models;

/// <summary>Represents the server-owned monotonic trust anchor for one canonical workspace capability catalog.</summary>
/// <param name="WorkspaceIdentity">The canonical workspace identity digest.</param>
/// <param name="CurrentGeneration">The only generation eligible to be current.</param>
/// <param name="CurrentContentDigest">The authenticated digest for the current generation.</param>
/// <param name="PreviousGeneration">The optional immediately previous recovery generation.</param>
/// <param name="PreviousContentDigest">The optional immediately previous recovery digest.</param>
public sealed record CapabilityCatalogTrustState(string WorkspaceIdentity, long CurrentGeneration, string CurrentContentDigest, long? PreviousGeneration, string? PreviousContentDigest);
