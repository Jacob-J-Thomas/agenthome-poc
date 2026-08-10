using EmbodySense.Core.Common.Authority.Grants;

namespace EmbodySense.Core.Persistence.Authority;

/// <summary>Defines bounded schema-version-1 authority-profile persistence limits.</summary>
public static class AuthorityProfileStoreLimits
{
    /// <summary>Gets the maximum profiles retained by one workspace.</summary>
    public const int MaximumProfiles = 512;
    /// <summary>Gets the maximum immutable revisions retained for one profile.</summary>
    public const int MaximumRevisionsPerProfile = 128;
    /// <summary>Gets the maximum immutable operation receipts retained without eviction.</summary>
    public const int MaximumOperationReceipts = 4_096;
    /// <summary>Gets the maximum grants retained by one workspace authority ledger.</summary>
    public const int MaximumGrants = AuthorityGrantContractLimits.MaxGrantsPerStore;
    /// <summary>Gets the maximum immutable revisions retained for one grant.</summary>
    public const int MaximumRevisionsPerGrant = AuthorityGrantContractLimits.MaxRevisionsPerGrant;
    /// <summary>Gets the maximum append-only grant operations retained without eviction.</summary>
    public const int MaximumGrantOperationReceipts = AuthorityGrantContractLimits.MaxOperationsPerStore;
    /// <summary>Gets the maximum canonical operation identifier length.</summary>
    public const int MaximumOperationIdCharacters = AuthorityGrantContractLimits.MaxOperationIdCharacters;
    /// <summary>Gets the maximum persisted authority artifact size.</summary>
    public const int MaximumArtifactUtf8Bytes = 4 * 1024 * 1024;
}
