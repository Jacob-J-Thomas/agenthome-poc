namespace EmbodySense.Core.Application.HumanInput.Responses.Models;

/// <summary>Returns one atomic response-store commit disposition with exact durable proof when available.</summary>
/// <param name="Status">The closed commit outcome.</param>
/// <param name="StoreGeneration">The observed workspace-global ledger generation.</param>
/// <param name="StoredOperation">The exact committed, replayed, or conflicting response operation when available.</param>
/// <param name="Snapshot">The exact resulting or observed request/response snapshot when available.</param>
public sealed record HumanInputResponseLifecycleStoreCommitResult(
    HumanInputResponseLifecycleStoreCommitStatus Status,
    long StoreGeneration,
    HumanInputResponseLifecycleStoredOperation? StoredOperation,
    HumanInputResponseLifecycleStoreSnapshot? Snapshot);
