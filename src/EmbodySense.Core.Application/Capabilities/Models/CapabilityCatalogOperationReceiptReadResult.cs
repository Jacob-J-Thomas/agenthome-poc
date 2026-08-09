namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Returns the bounded durable operation receipts for one capability or a fail-closed availability result.</summary>
/// <param name="Status">The catalog read status.</param>
/// <param name="CatalogGeneration">The exact authenticated document generation containing the receipts, or <see langword="null"/> when unavailable.</param>
/// <param name="Receipts">The integrity-proved receipts ordered by operation identity.</param>
/// <param name="Detail">A bounded non-sensitive explanation.</param>
public sealed record CapabilityCatalogOperationReceiptReadResult(CapabilityCatalogReadStatus Status, long? CatalogGeneration, IReadOnlyList<CapabilityCatalogOperationReceipt> Receipts, string Detail)
{
    /// <summary>Gets a defensive read-only copy of the receipts.</summary>
    public IReadOnlyList<CapabilityCatalogOperationReceipt> Receipts { get; } = Receipts is null ? null! : Array.AsReadOnly(Receipts.ToArray());
}
