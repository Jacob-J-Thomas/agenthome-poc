namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Provides a stable non-sensitive capability posture error.</summary>
/// <param name="Code">The stable machine-readable error code.</param>
/// <param name="Message">The stable bounded public explanation.</param>
public sealed record CapabilityPostureError(string Code, string Message);
