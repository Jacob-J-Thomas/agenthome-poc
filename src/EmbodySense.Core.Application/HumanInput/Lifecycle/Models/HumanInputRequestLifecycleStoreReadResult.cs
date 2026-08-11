namespace EmbodySense.Core.Application.HumanInput.Lifecycle.Models;

/// <summary>Returns one optimistic Human Input lifecycle observation and any workspace-global operation binding.</summary>
/// <param name="Status">The closed read outcome.</param>
/// <param name="StoreGeneration">The nonnegative workspace-global store generation.</param>
/// <param name="PrimarySnapshot">The exact target lifecycle snapshot when available.</param>
/// <param name="RelatedSnapshot">The exact directly related supersession lifecycle snapshot when available.</param>
/// <param name="ExistingOperation">The globally retained operation with the requested identity, when present.</param>
public sealed record HumanInputRequestLifecycleStoreReadResult(
    HumanInputRequestLifecycleStoreReadStatus Status,
    long StoreGeneration,
    HumanInputRequestLifecycleStoreSnapshot? PrimarySnapshot,
    HumanInputRequestLifecycleStoreSnapshot? RelatedSnapshot,
    HumanInputRequestLifecycleStoredOperation? ExistingOperation);
