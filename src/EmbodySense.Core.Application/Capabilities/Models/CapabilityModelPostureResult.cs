namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Returns deterministic assigned model context or a stable non-sensitive error.</summary>
/// <param name="Status">The bounded query status.</param>
/// <param name="Capabilities">Only the exact assigned and currently authorized pins.</param>
/// <param name="CanonicalJson">The deterministic schema-version-1 model context.</param>
/// <param name="Error">The stable error when context cannot be returned safely.</param>
public sealed record CapabilityModelPostureResult(CapabilityPostureReadStatus Status, IReadOnlyList<CapabilityModelPostureProjection> Capabilities, string CanonicalJson, CapabilityPostureError? Error)
{
    /// <summary>Gets a defensive read-only capability snapshot.</summary>
    public IReadOnlyList<CapabilityModelPostureProjection> Capabilities { get; } = Array.AsReadOnly((Capabilities ?? []).ToArray());
}
