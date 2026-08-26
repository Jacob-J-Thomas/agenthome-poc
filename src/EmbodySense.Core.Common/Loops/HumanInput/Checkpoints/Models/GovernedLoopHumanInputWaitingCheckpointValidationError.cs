namespace EmbodySense.Core.Common.Loops.HumanInput.Checkpoints.Models;

/// <summary>Describes one deterministic Human Input waiting-checkpoint contract violation.</summary>
/// <param name="Code">The stable machine-readable violation code.</param>
/// <param name="Path">The bounded contract path containing the violation.</param>
/// <param name="Message">The safe explanatory message, which contains no untrusted request content.</param>
public sealed record GovernedLoopHumanInputWaitingCheckpointValidationError(string Code, string Path, string Message);
