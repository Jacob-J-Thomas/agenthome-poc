namespace EmbodySense.Core.Persistence.HumanInput.Policies.Models;

/// <summary>Returns the outcome and observed generation of one immutable Human Input policy write.</summary>
/// <param name="Status">The closed optimistic-write result.</param>
/// <param name="StoreGeneration">The exact observed store generation, or zero when unavailable.</param>
public sealed record HumanInputPolicyFileStoreWriteResult(HumanInputPolicyFileStoreWriteStatus Status, long StoreGeneration);
