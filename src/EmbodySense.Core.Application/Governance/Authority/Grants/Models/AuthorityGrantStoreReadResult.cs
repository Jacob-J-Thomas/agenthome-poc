namespace EmbodySense.Core.Application.Governance.Authority.Grants.Models;

/// <summary>Returns one optimistic grant-store observation and any workspace-global operation binding.</summary>
/// <param name="Status">The closed read outcome.</param>
/// <param name="StoreGeneration">The nonnegative workspace-global store generation.</param>
/// <param name="Snapshot">The exact current grant snapshot when available.</param>
/// <param name="ExistingOperation">The globally retained operation with the requested identity, when present.</param>
public sealed record AuthorityGrantStoreReadResult(
    AuthorityGrantStoreReadStatus Status,
    long StoreGeneration,
    AuthorityGrantStoreSnapshot? Snapshot,
    AuthorityGrantStoredOperation? ExistingOperation);
