namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Returns a read-only lifecycle impact projection or stable non-sensitive error.</summary>
/// <param name="Status">The bounded read status.</param>
/// <param name="Preview">The preview when complete proved evidence was available.</param>
/// <param name="Error">The stable error when no preview is returned.</param>
public sealed record CapabilityPosturePreviewResult(CapabilityPostureReadStatus Status, CapabilityPosturePreviewProjection? Preview, CapabilityPostureError? Error);
