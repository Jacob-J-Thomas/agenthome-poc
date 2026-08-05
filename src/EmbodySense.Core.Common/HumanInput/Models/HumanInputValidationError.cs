namespace EmbodySense.Core.Common.HumanInput.Models;

/// <summary>
/// Describes one deterministic human-input contract validation error.
/// </summary>
/// <param name="Code">The stable machine-readable error code.</param>
/// <param name="Field">The invalid contract field path.</param>
/// <param name="Message">The bounded human-readable failure explanation.</param>
public sealed record HumanInputValidationError(string Code, string Field, string Message);
