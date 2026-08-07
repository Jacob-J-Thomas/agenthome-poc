namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Returns a bounded catalog page or a fail-closed availability result.</summary>
/// <param name="Status">The read status.</param>
/// <param name="Page">The page when trustworthy state was available.</param>
/// <param name="Detail">A bounded non-sensitive explanation.</param>
public sealed record CapabilityCatalogReadResult(CapabilityCatalogReadStatus Status, CapabilityCatalogPage? Page, string Detail);
