namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Returns one safe capability posture or a stable non-sensitive error.</summary>
/// <param name="Status">The bounded read status.</param>
/// <param name="Posture">The posture when trustworthy evidence is available.</param>
/// <param name="Error">The stable error when no posture is returned.</param>
public sealed record CapabilityPostureQueryResult(CapabilityPostureReadStatus Status, CapabilityPostureProjection? Posture, CapabilityPostureError? Error);
