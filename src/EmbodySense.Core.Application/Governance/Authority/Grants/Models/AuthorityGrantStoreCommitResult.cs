namespace EmbodySense.Core.Application.Governance.Authority.Grants.Models;

/// <summary>Returns one atomic grant-store commit disposition with exact durable proof when available.</summary>
/// <param name="Status">The closed commit outcome.</param>
/// <param name="StoreGeneration">The observed workspace-global generation.</param>
/// <param name="StoredOperation">The exact committed, replayed, or conflicting operation proof when available.</param>
/// <param name="Snapshot">The exact resulting or observed target-grant snapshot when available.</param>
public sealed record AuthorityGrantStoreCommitResult(
    AuthorityGrantStoreCommitStatus Status,
    long StoreGeneration,
    AuthorityGrantStoredOperation? StoredOperation,
    AuthorityGrantStoreSnapshot? Snapshot);
