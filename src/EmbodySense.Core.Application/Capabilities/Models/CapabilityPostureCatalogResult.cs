namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Returns one bounded deterministic page of safe administrative capability posture.</summary>
/// <param name="Status">The page read status.</param>
/// <param name="CatalogRevision">The exact catalog revision when available.</param>
/// <param name="Entries">The ordered safe posture entries.</param>
/// <param name="NextCursor">The exclusive next-page cursor when another page remains.</param>
/// <param name="Error">The stable error when the page cannot be returned.</param>
public sealed record CapabilityPostureCatalogResult(CapabilityPostureReadStatus Status, long? CatalogRevision, IReadOnlyList<CapabilityPostureProjection> Entries, string? NextCursor, CapabilityPostureError? Error)
{
    /// <summary>Gets a defensive read-only entry snapshot.</summary>
    public IReadOnlyList<CapabilityPostureProjection> Entries { get; } = Array.AsReadOnly((Entries ?? []).ToArray());
}
