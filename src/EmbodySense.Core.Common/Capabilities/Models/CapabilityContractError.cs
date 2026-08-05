namespace EmbodySense.Core.Common.Capabilities.Models;

/// <summary>
/// Describes one structured capability-contract rejection.
/// </summary>
/// <param name="Code">The stable machine-readable error code.</param>
/// <param name="Field">The rejected field path.</param>
/// <param name="Message">The bounded human-readable explanation.</param>
public sealed record CapabilityContractError(string Code, string Field, string Message);
