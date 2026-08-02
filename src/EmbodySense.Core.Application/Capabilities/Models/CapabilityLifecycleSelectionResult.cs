namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Returns a browser-safe selection outcome and its server-created durable preview.</summary>
/// <param name="Status">The selection outcome.</param>
/// <param name="Preview">The full server-owned preview only when lifecycle persistence was reached.</param>
/// <param name="Detail">A bounded operator-facing explanation.</param>
public sealed record CapabilityLifecycleSelectionResult(CapabilityLifecycleSelectionStatus Status, CapabilityLifecyclePreview? Preview, string Detail);
