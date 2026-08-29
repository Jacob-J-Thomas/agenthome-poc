namespace EmbodySense.Core.Application.HumanInput.Catalog.Models;

/// <summary>Selects one bounded canonical Human Input catalog page.</summary>
/// <param name="MaximumCount">The requested page size, which must be within the catalog's finite bound.</param>
/// <param name="Cursor">An optional opaque continuation from an unchanged authenticated ledger generation.</param>
public sealed record HumanInputRequestCatalogPageRequest(int MaximumCount, string? Cursor = null);
