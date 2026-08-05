namespace EmbodySense.Core.Common.Capabilities.Models;

/// <summary>
/// Declares bounded resource requirements; these declarations do not reserve or authorize resources.
/// </summary>
/// <param name="MaxExecutionMilliseconds">The maximum execution duration in milliseconds.</param>
/// <param name="MaxMemoryBytes">The maximum memory requirement in bytes.</param>
/// <param name="MaxOutputBytes">The maximum output size in bytes.</param>
/// <param name="MaxConcurrency">The maximum concurrent execution count.</param>
public sealed record CapabilityResourceLimits(int MaxExecutionMilliseconds, long MaxMemoryBytes, int MaxOutputBytes, int MaxConcurrency);
