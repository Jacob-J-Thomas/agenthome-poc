namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Contains one bounded deterministic catalog page.</summary>
/// <param name="CatalogRevision">The current catalog state revision.</param>
/// <param name="Entries">The capability entries ordered by canonical identifier.</param>
/// <param name="NextCursor">The last returned identifier when another page remains.</param>
public sealed record CapabilityCatalogPage(long CatalogRevision, IReadOnlyList<CapabilityCatalogEntry> Entries, string? NextCursor)
{
    /// <summary>Gets a defensive read-only copy of the entries.</summary>
    public IReadOnlyList<CapabilityCatalogEntry> Entries { get; } = CapabilityCatalogPageSnapshot.Capture(Entries);
}
