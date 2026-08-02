namespace EmbodySense.Core.Startup.Capabilities.Models;

/// <summary>Returns one exact safe posture or a stable surface-neutral error.</summary>
/// <param name="Status">The stable read status token.</param>
/// <param name="Capability">The safe posture when available.</param>
/// <param name="Error">The stable error when no posture is returned.</param>
public sealed record CapabilityPostureResponse(string Status, CapabilityPostureSnapshot? Capability, CapabilityPostureError? Error);
