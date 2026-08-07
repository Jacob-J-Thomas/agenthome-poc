namespace EmbodySense.Core.Application.Loops.GraphValidation.Models;

/// <summary>Identifies the exact catalog and authority evidence used for one deterministic graph decision.</summary>
/// <param name="CatalogHash">The canonical catalog snapshot SHA-256 digest.</param>
/// <param name="AuthorityHash">The canonical authority snapshot SHA-256 digest.</param>
/// <param name="CombinedHash">The SHA-256 digest binding both snapshot digests.</param>
public sealed record GovernedLoopGraphValidationEvidence(string CatalogHash, string AuthorityHash, string CombinedHash);
