using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints.Models;

namespace EmbodySense.Core.Common.Loops.HumanInput.Checkpoints;

/// <summary>Classifies valid exact checkpoint and evidence replay versus unsafe divergent reuse or semantically invalid artifacts without selecting any runtime action.</summary>
public static class GovernedLoopHumanInputWaitingCheckpointReplayClassifier
{
    /// <summary>Classifies proposed checkpoint reuse against one retained checkpoint by exact stable checkpoint identity and canonical hash.</summary>
    /// <param name="retained">The retained checkpoint.</param>
    /// <param name="proposed">The proposed checkpoint.</param>
    /// <returns>Exact replay only for independently valid canonical equal identities and hashes; otherwise new, divergent, or invalid.</returns>
    public static GovernedLoopHumanInputWaitingCheckpointReplayDisposition Classify(
        GovernedLoopHumanInputWaitingCheckpoint? retained,
        GovernedLoopHumanInputWaitingCheckpoint? proposed)
    {
        if (!GovernedLoopHumanInputWaitingCheckpointContractValidator.Validate(retained).IsValid
            || !GovernedLoopHumanInputWaitingCheckpointContractValidator.Validate(proposed).IsValid)
        {
            return GovernedLoopHumanInputWaitingCheckpointReplayDisposition.Invalid;
        }

        if (!string.Equals(retained!.Binding.CheckpointId, proposed!.Binding.CheckpointId, StringComparison.Ordinal))
        {
            return GovernedLoopHumanInputWaitingCheckpointReplayDisposition.New;
        }

        return Exact(retained.CheckpointHash, proposed.CheckpointHash, GovernedLoopHumanInputWaitingCheckpointContractHash.Matches(retained), GovernedLoopHumanInputWaitingCheckpointContractHash.Matches(proposed));
    }

    /// <summary>Classifies proposed evidence reuse against one retained evidence sequence by canonical evidence hash.</summary>
    /// <param name="retained">The retained evidence entry.</param>
    /// <param name="proposed">The proposed evidence entry.</param>
    /// <returns>Exact replay only for independently valid canonical equal sequence identities and hashes; otherwise new, divergent, or invalid.</returns>
    public static GovernedLoopHumanInputWaitingCheckpointReplayDisposition Classify(
        GovernedLoopHumanInputWaitingCheckpointEvidence? retained,
        GovernedLoopHumanInputWaitingCheckpointEvidence? proposed)
    {
        if (!GovernedLoopHumanInputWaitingCheckpointContractValidator.ValidateEvidence(retained).IsValid
            || !GovernedLoopHumanInputWaitingCheckpointContractValidator.ValidateEvidence(proposed).IsValid)
        {
            return GovernedLoopHumanInputWaitingCheckpointReplayDisposition.Invalid;
        }

        if (retained!.Sequence != proposed!.Sequence)
        {
            return GovernedLoopHumanInputWaitingCheckpointReplayDisposition.New;
        }

        return Exact(retained.EvidenceHash, proposed.EvidenceHash, GovernedLoopHumanInputWaitingCheckpointContractHash.Matches(retained), GovernedLoopHumanInputWaitingCheckpointContractHash.Matches(proposed));
    }

    private static GovernedLoopHumanInputWaitingCheckpointReplayDisposition Exact(string retainedHash, string proposedHash, bool retainedMatches, bool proposedMatches)
        => retainedMatches && proposedMatches && string.Equals(retainedHash, proposedHash, StringComparison.Ordinal)
            ? GovernedLoopHumanInputWaitingCheckpointReplayDisposition.ExactReplay
            : GovernedLoopHumanInputWaitingCheckpointReplayDisposition.DivergentReuse;
}
