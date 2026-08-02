namespace EmbodySense.Core.Startup.Capabilities.Models;

/// <summary>Provides a stable surface-neutral capability posture error.</summary>
/// <param name="Code">The stable machine-readable code.</param>
/// <param name="Message">The stable bounded public explanation.</param>
public sealed record CapabilityPostureError(string Code, string Message);
