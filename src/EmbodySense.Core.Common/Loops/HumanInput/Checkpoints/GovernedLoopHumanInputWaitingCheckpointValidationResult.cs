using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints.Models;

namespace EmbodySense.Core.Common.Loops.HumanInput.Checkpoints;

/// <summary>Contains deterministic validation failures for one Human Input waiting-checkpoint artifact.</summary>
/// <param name="Errors">The ordered validation failures.</param>
public sealed record GovernedLoopHumanInputWaitingCheckpointValidationResult(IReadOnlyList<GovernedLoopHumanInputWaitingCheckpointValidationError> Errors)
{
    /// <summary>Gets whether no contract violations were found.</summary>
    public bool IsValid => Errors.Count == 0;
}
