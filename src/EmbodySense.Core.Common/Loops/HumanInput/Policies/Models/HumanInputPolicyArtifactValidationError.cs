namespace EmbodySense.Core.Common.Loops.HumanInput.Policies.Models;

/// <summary>Describes one deterministic validation failure in a Human Input policy artifact.</summary>
/// <param name="Code">The stable machine-readable failure code.</param>
/// <param name="Path">The bounded contract path containing the failure.</param>
/// <param name="Message">The safe explanatory message.</param>
public sealed record HumanInputPolicyArtifactValidationError(string Code, string Path, string Message);
