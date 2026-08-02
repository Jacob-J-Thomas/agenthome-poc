namespace EmbodySense.Core.Startup.Capabilities.Models;

/// <summary>Returns deterministic assignment-filtered model context or a stable non-sensitive error.</summary>
/// <param name="Status">The stable read status token.</param>
/// <param name="Capabilities">Only exact assigned and currently authorized capabilities.</param>
/// <param name="CanonicalJson">The deterministic schema-version-1 model context.</param>
/// <param name="Error">The stable error when context cannot be returned safely.</param>
public sealed record CapabilityModelPostureResponse(string Status, IReadOnlyList<CapabilityModelPostureSnapshot> Capabilities, string CanonicalJson, CapabilityPostureError? Error)
{
    /// <summary>Gets a defensive read-only capability snapshot.</summary>
    public IReadOnlyList<CapabilityModelPostureSnapshot> Capabilities { get; } = Array.AsReadOnly((Capabilities ?? []).ToArray());
}
