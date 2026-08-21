using EmbodySense.Core.Common.Authority.Delegation.Models;

namespace EmbodySense.Core.Common.Authority.Delegation;

/// <summary>Retains hash-only, server-recomputable evidence that delegated authority does not exceed exact maxima.</summary>
public sealed record AuthorityDelegationSubsetProof
{
    /// <summary>Creates a proof with a bounded defensive narrowing snapshot.</summary>
    /// <param name="parentEvidenceHash">The exact parent-evidence content hash.</param>
    /// <param name="parentAuthorityScopeHash">The hash of the parent effective ceiling and exact pins.</param>
    /// <param name="delegatedAuthorityScopeHash">The hash of the delegated ceiling and exact pins.</param>
    /// <param name="targetMaximumEvidenceHash">The server-resolved exact target-maximum evidence hash.</param>
    /// <param name="narrowingDimensions">The canonical list of strictly narrowed authority dimensions.</param>
    /// <param name="contentHash">The canonical hash over the proof except this field.</param>
    public AuthorityDelegationSubsetProof(
        string parentEvidenceHash,
        string parentAuthorityScopeHash,
        string delegatedAuthorityScopeHash,
        string targetMaximumEvidenceHash,
        IReadOnlyList<AuthorityDelegationNarrowingDimension> narrowingDimensions,
        string contentHash)
    {
        ParentEvidenceHash = parentEvidenceHash;
        ParentAuthorityScopeHash = parentAuthorityScopeHash;
        DelegatedAuthorityScopeHash = delegatedAuthorityScopeHash;
        TargetMaximumEvidenceHash = targetMaximumEvidenceHash;
        NarrowingDimensions = AuthorityDelegationContractCopy.Snapshot(narrowingDimensions, AuthorityDelegationContractLimits.MaxNarrowingDimensions);
        ContentHash = contentHash;
    }

    /// <summary>Gets the exact parent-evidence content hash.</summary>
    public string ParentEvidenceHash { get; }

    /// <summary>Gets the hash of the broader parent authority scope without exposing that scope.</summary>
    public string ParentAuthorityScopeHash { get; }

    /// <summary>Gets the hash of the delegated authority scope.</summary>
    public string DelegatedAuthorityScopeHash { get; }

    /// <summary>Gets the server-resolved exact target-maximum evidence hash.</summary>
    public string TargetMaximumEvidenceHash { get; }

    /// <summary>Gets the canonical strictly narrowed authority dimensions.</summary>
    public IReadOnlyList<AuthorityDelegationNarrowingDimension> NarrowingDimensions { get; }

    /// <summary>Gets the canonical proof content hash.</summary>
    public string ContentHash { get; init; }
}
