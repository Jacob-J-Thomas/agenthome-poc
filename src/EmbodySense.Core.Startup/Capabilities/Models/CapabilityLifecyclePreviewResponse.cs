namespace EmbodySense.Core.Startup.Capabilities.Models;

/// <summary>Returns one safe durable lifecycle preview outcome.</summary>
/// <param name="Status">The stable outcome token.</param>
/// <param name="Preview">The safe preview when current or replayed evidence is available.</param>
/// <param name="Error">The stable non-sensitive error when no preview is available.</param>
public sealed record CapabilityLifecyclePreviewResponse(string Status, CapabilityLifecyclePreviewSnapshot? Preview, CapabilityPostureError? Error);
