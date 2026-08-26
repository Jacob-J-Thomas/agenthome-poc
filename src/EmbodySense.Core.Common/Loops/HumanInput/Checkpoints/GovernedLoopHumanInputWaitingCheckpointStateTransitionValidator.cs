using System.Text.Json;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints.Models;

namespace EmbodySense.Core.Common.Loops.HumanInput.Checkpoints;

/// <summary>Validates one closed single-boundary successor transition without claiming, recovering, notifying, resuming, or performing an effect.</summary>
public static class GovernedLoopHumanInputWaitingCheckpointStateTransitionValidator
{
    /// <summary>Validates null-to-pending publication, exact replay, one pending terminal boundary, answered-to-terminal consumption, and terminal exact replay only.</summary>
    /// <param name="previous">The retained checkpoint state, or null before publication.</param>
    /// <param name="candidate">The proposed checkpoint state.</param>
    /// <returns>Every deterministic transition violation.</returns>
    public static GovernedLoopHumanInputWaitingCheckpointValidationResult ValidateTransition(
        GovernedLoopHumanInputWaitingCheckpoint? previous,
        GovernedLoopHumanInputWaitingCheckpoint? candidate)
    {
        var errors = new List<GovernedLoopHumanInputWaitingCheckpointValidationError>();
        if (previous is not null)
        {
            errors.AddRange(GovernedLoopHumanInputWaitingCheckpointContractValidator.Validate(previous).Errors);
        }
        errors.AddRange(GovernedLoopHumanInputWaitingCheckpointContractValidator.Validate(candidate).Errors);
        if (candidate is null || errors.Count != 0)
        {
            return new GovernedLoopHumanInputWaitingCheckpointValidationResult(errors);
        }

        if (previous is null)
        {
            if (candidate.Posture != GovernedLoopHumanInputWaitingCheckpointPosture.Pending)
            {
                Add(errors, "initial_transition_must_be_pending", "$.posture", "The first checkpoint state may only publish the immutable pending posture.");
            }
            return new GovernedLoopHumanInputWaitingCheckpointValidationResult(errors);
        }

        if (GovernedLoopHumanInputWaitingCheckpointReplayClassifier.Classify(previous, candidate) == GovernedLoopHumanInputWaitingCheckpointReplayDisposition.ExactReplay)
        {
            return new GovernedLoopHumanInputWaitingCheckpointValidationResult(errors);
        }
        if (!SameImmutableCoordinates(previous, candidate))
        {
            Add(errors, "immutable_coordinate_rebound", "$", "A retained checkpoint cannot rebind graph, run, frontier, configuration, request, or checkpoint identity.");
            return new GovernedLoopHumanInputWaitingCheckpointValidationResult(errors);
        }
        if (IsTerminal(previous.Posture))
        {
            Add(errors, "terminal_exact_replay_required", "$", "Expired, cancelled, superseded, and terminal checkpoint states permit exact canonical replay only.");
            return new GovernedLoopHumanInputWaitingCheckpointValidationResult(errors);
        }
        if (candidate.Evidence.Length != previous.Evidence.Length + 1 || !HasExactEvidencePrefix(previous, candidate))
        {
            Add(errors, "single_boundary_required", "$.evidence", "A successor must preserve exact evidence and append exactly one legal boundary.");
            return new GovernedLoopHumanInputWaitingCheckpointValidationResult(errors);
        }

        var legal = previous.Posture switch
        {
            GovernedLoopHumanInputWaitingCheckpointPosture.Pending => candidate.Posture is GovernedLoopHumanInputWaitingCheckpointPosture.AnsweredNotResumed or GovernedLoopHumanInputWaitingCheckpointPosture.Expired or GovernedLoopHumanInputWaitingCheckpointPosture.Cancelled or GovernedLoopHumanInputWaitingCheckpointPosture.Superseded,
            GovernedLoopHumanInputWaitingCheckpointPosture.AnsweredNotResumed => candidate.Posture == GovernedLoopHumanInputWaitingCheckpointPosture.Terminal,
            _ => false,
        };
        if (!legal)
        {
            Add(errors, "illegal_posture_transition", "$.posture", "Only pending-to-answered/expired/cancelled/superseded or answered-not-resumed-to-terminal transitions are legal.");
        }

        return new GovernedLoopHumanInputWaitingCheckpointValidationResult(errors);
    }

    private static bool SameImmutableCoordinates(GovernedLoopHumanInputWaitingCheckpoint previous, GovernedLoopHumanInputWaitingCheckpoint candidate)
        => Equals(previous.Binding, candidate.Binding)
            && string.Equals(previous.Request.RequestId, candidate.Request.RequestId, StringComparison.Ordinal)
            && string.Equals(previous.Request.RequestVersionId, candidate.Request.RequestVersionId, StringComparison.Ordinal)
            && string.Equals(previous.Request.RequestHash, candidate.Request.RequestHash, StringComparison.Ordinal)
            && ConfigurationEquals(previous, candidate);

    private static bool ConfigurationEquals(GovernedLoopHumanInputWaitingCheckpoint previous, GovernedLoopHumanInputWaitingCheckpoint candidate)
        => string.Equals(JsonSerializer.Serialize(previous.NodeConfiguration), JsonSerializer.Serialize(candidate.NodeConfiguration), StringComparison.Ordinal);

    private static bool HasExactEvidencePrefix(GovernedLoopHumanInputWaitingCheckpoint previous, GovernedLoopHumanInputWaitingCheckpoint candidate)
        => previous.Evidence.Select((item, index) => GovernedLoopHumanInputWaitingCheckpointReplayClassifier.Classify(item, candidate.Evidence[index]) == GovernedLoopHumanInputWaitingCheckpointReplayDisposition.ExactReplay).All(value => value);

    private static bool IsTerminal(GovernedLoopHumanInputWaitingCheckpointPosture posture)
        => posture is GovernedLoopHumanInputWaitingCheckpointPosture.Expired or GovernedLoopHumanInputWaitingCheckpointPosture.Cancelled or GovernedLoopHumanInputWaitingCheckpointPosture.Superseded or GovernedLoopHumanInputWaitingCheckpointPosture.Terminal;

    private static void Add(List<GovernedLoopHumanInputWaitingCheckpointValidationError> errors, string code, string path, string message)
        => errors.Add(new GovernedLoopHumanInputWaitingCheckpointValidationError(code, path, message));
}
