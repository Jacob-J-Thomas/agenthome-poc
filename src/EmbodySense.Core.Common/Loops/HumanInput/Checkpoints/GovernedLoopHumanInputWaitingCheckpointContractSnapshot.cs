using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints.Models;

namespace EmbodySense.Core.Common.Loops.HumanInput.Checkpoints;

/// <summary>Captures independent bounded Human Input waiting-checkpoint snapshots before validation, replay comparison, JSON serialization, or durable storage.</summary>
public static class GovernedLoopHumanInputWaitingCheckpointContractSnapshot
{
    /// <summary>Captures a deeply independent validated checkpoint snapshot.</summary>
    /// <param name="checkpoint">The potentially caller-owned checkpoint.</param>
    /// <param name="snapshot">The detached validated snapshot when successful.</param>
    /// <param name="validation">The deterministic snapshot or checkpoint validation failures.</param>
    /// <returns><see langword="true"/> when an independent complete snapshot was captured; otherwise, <see langword="false"/>.</returns>
    public static bool TryCapture(
        GovernedLoopHumanInputWaitingCheckpoint? checkpoint,
        out GovernedLoopHumanInputWaitingCheckpoint? snapshot,
        out GovernedLoopHumanInputWaitingCheckpointValidationResult validation)
    {
        if (checkpoint is null)
        {
            snapshot = null;
            validation = GovernedLoopHumanInputWaitingCheckpointContractValidator.Validate(null);
            return false;
        }

        try
        {
            snapshot = new GovernedLoopHumanInputWaitingCheckpoint(checkpoint.SchemaVersion, checkpoint.Binding, checkpoint.NodeConfiguration, checkpoint.Request, checkpoint.Posture, checkpoint.Evidence, checkpoint.CheckpointHash);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IndexOutOfRangeException or NullReferenceException)
        {
            snapshot = null;
            validation = new GovernedLoopHumanInputWaitingCheckpointValidationResult([new GovernedLoopHumanInputWaitingCheckpointValidationError("checkpoint_snapshot_unstable", "$", "Checkpoint shape changed while its bounded snapshot was captured.")]);
            return false;
        }

        validation = GovernedLoopHumanInputWaitingCheckpointContractValidator.Validate(snapshot);
        if (validation.IsValid)
        {
            return true;
        }

        snapshot = null;
        return false;
    }
}
