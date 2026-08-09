namespace EmbodySense.Core.Startup.Capabilities.Models;

/// <summary>Returns one bounded deterministic page of safe administrative capability posture.</summary>
/// <param name="Status">The stable read status token.</param>
/// <param name="CatalogRevision">The exact catalog revision when available.</param>
/// <param name="Capabilities">The ordered safe postures.</param>
/// <param name="NextCursor">The exclusive next-page cursor.</param>
/// <param name="Error">The stable error when no page is returned.</param>
public sealed record CapabilityPostureCatalogResponse(string Status, long? CatalogRevision, IReadOnlyList<CapabilityPostureSnapshot> Capabilities, string? NextCursor, CapabilityPostureError? Error)
{
    /// <summary>Gets a defensive read-only capability snapshot.</summary>
    public IReadOnlyList<CapabilityPostureSnapshot> Capabilities { get; } = Array.AsReadOnly((Capabilities ?? []).ToArray());
}
