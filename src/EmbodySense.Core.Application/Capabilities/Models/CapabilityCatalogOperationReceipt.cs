namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Projects one integrity-proved durable catalog operation outcome without exposing catalog authentication material.</summary>
/// <param name="OperationId">The durable idempotency identity.</param>
/// <param name="Outcome">The committed mutation outcome.</param>
/// <param name="CatalogRevision">The catalog revision observed by the committed outcome.</param>
/// <param name="Entry">The exact target entry snapshot retained by the receipt.</param>
public sealed record CapabilityCatalogOperationReceipt(string OperationId, CapabilityCatalogMutationStatus Outcome, long CatalogRevision, CapabilityCatalogEntry Entry);
