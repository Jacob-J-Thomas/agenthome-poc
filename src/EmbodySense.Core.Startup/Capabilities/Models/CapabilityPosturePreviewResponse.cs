namespace EmbodySense.Core.Startup.Capabilities.Models;

/// <summary>Returns a read-only lifecycle impact projection or stable error.</summary>
/// <param name="Status">The stable read status token.</param>
/// <param name="Preview">The preview when complete proved evidence was available.</param>
/// <param name="Error">The stable error when no preview is returned.</param>
public sealed record CapabilityPosturePreviewResponse(string Status, CapabilityPosturePreviewSnapshot? Preview, CapabilityPostureError? Error);
