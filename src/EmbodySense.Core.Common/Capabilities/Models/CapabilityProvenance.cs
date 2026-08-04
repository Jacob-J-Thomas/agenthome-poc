namespace EmbodySense.Core.Common.Capabilities.Models;

/// <summary>
/// Describes safe, non-authoritative implementation provenance.
/// </summary>
/// <param name="Kind">The provenance category.</param>
/// <param name="SourceUri">The canonical absolute source URI without user information, query, or fragment.</param>
/// <param name="SourceRevision">The optional bounded source revision.</param>
/// <param name="Integrity">The optional content integrity digest.</param>
/// <remarks>Provenance is evidence only; it does not assert verification or trust.</remarks>
public sealed record CapabilityProvenance(CapabilityProvenanceKind Kind, string SourceUri, string? SourceRevision, CapabilityIntegrityDigest? Integrity);
