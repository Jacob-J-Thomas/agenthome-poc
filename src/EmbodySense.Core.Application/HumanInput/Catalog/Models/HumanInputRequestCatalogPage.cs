namespace EmbodySense.Core.Application.HumanInput.Catalog.Models;

/// <summary>Returns one bounded canonical Human Input catalog page.</summary>
/// <param name="Status">The closed page-read disposition.</param>
/// <param name="StoreGeneration">The authenticated ledger generation when safely established.</param>
/// <param name="Entries">The ordered request aggregates from that one generation.</param>
/// <param name="NextCursor">The opaque next-page cursor, or null when no additional aggregate exists.</param>
public sealed record HumanInputRequestCatalogPage(
    HumanInputRequestCatalogPageStatus Status,
    long StoreGeneration,
    IReadOnlyList<HumanInputRequestCatalogEntry> Entries,
    string? NextCursor)
{
    /// <summary>Gets a defensive immutable copy of the page entries.</summary>
    public IReadOnlyList<HumanInputRequestCatalogEntry> Entries { get; } = Entries is null ? null! : Array.AsReadOnly(Entries.ToArray());
}
