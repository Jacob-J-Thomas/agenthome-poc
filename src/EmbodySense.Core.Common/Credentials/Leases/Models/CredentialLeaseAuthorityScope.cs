namespace EmbodySense.Core.Common.Credentials.Leases.Models;

/// <summary>Binds a credential lease to exact admitted and freshly evaluated authority evidence.</summary>
public sealed record CredentialLeaseAuthorityScope(
    string AuthorityProofId,
    string AuthorityProofHash,
    string AuthorityProfileId,
    long AuthorityProfileRevision,
    string AuthorityProfileHash,
    string GrantId,
    long GrantRevision,
    string GrantHash,
    string AuthorityBoundaryHash,
    string CurrentAuthorityDecisionHash,
    string? DelegationEnvelopeHash);
