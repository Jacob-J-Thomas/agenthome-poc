namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Returns one structured catalog mutation outcome.</summary>
/// <param name="Status">The outcome status.</param>
/// <param name="OperationId">The operation identity.</param>
/// <param name="CatalogRevision">The current catalog state revision when known.</param>
/// <param name="Entry">The resulting target entry when known.</param>
/// <param name="Detail">A bounded non-sensitive explanation.</param>
public sealed record CapabilityCatalogMutationResult(CapabilityCatalogMutationStatus Status, string OperationId, long? CatalogRevision, CapabilityCatalogEntry? Entry, string Detail);
