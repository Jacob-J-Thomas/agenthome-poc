namespace EmbodySense.Core.Application.HumanInput.Responses.Models;

/// <summary>Returns one optimistic Human Input response observation and any exact response-operation binding.</summary>
/// <param name="Status">The closed read disposition.</param>
/// <param name="StoreGeneration">The nonnegative workspace-global ledger generation.</param>
/// <param name="Snapshot">The exact current request/response snapshot when available.</param>
/// <param name="ExistingOperation">The globally retained exact-family operation when present.</param>
public sealed record HumanInputResponseLifecycleStoreReadResult(
    HumanInputResponseLifecycleStoreReadStatus Status,
    long StoreGeneration,
    HumanInputResponseLifecycleStoreSnapshot? Snapshot,
    HumanInputResponseLifecycleStoredOperation? ExistingOperation);
