namespace EmbodySense.Core.Persistence.HumanInput.Policies.Models;

/// <summary>Captures one authenticated schema-1 mutable catalog generation and its complete immutable membership.</summary>
internal sealed record HumanInputPolicyFileStoreGeneration(long StoreGeneration, IReadOnlyList<HumanInputPolicyFileStoreCatalogEntry> Artifacts);
