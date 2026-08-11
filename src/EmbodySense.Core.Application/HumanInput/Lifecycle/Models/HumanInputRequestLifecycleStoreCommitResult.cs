namespace EmbodySense.Core.Application.HumanInput.Lifecycle.Models;

/// <summary>Returns one atomic Human Input lifecycle commit disposition with exact durable proof when available.</summary>
/// <param name="Status">The closed commit outcome.</param>
/// <param name="StoreGeneration">The observed workspace-global store generation.</param>
/// <param name="StoredOperation">The exact committed, replayed, or conflicting operation proof when available.</param>
/// <param name="PrimarySnapshot">The exact resulting or observed target lifecycle snapshot when available.</param>
/// <param name="RelatedSnapshot">The exact resulting or observed related supersession lifecycle snapshot when available.</param>
public sealed record HumanInputRequestLifecycleStoreCommitResult(
    HumanInputRequestLifecycleStoreCommitStatus Status,
    long StoreGeneration,
    HumanInputRequestLifecycleStoredOperation? StoredOperation,
    HumanInputRequestLifecycleStoreSnapshot? PrimarySnapshot,
    HumanInputRequestLifecycleStoreSnapshot? RelatedSnapshot);
