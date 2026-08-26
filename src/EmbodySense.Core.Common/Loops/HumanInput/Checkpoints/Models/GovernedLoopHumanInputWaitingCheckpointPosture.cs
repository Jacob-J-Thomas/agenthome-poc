namespace EmbodySense.Core.Common.Loops.HumanInput.Checkpoints.Models;

/// <summary>Identifies the closed durable posture of one Human Input waiting checkpoint.</summary>
public enum GovernedLoopHumanInputWaitingCheckpointPosture
{
    /// <summary>No supported posture was supplied.</summary>
    Unknown = 0,

    /// <summary>The exact request is durably waiting for untrusted response data.</summary>
    Pending = 1,

    /// <summary>An exact answer selection is recorded but no runner has resumed work.</summary>
    AnsweredNotResumed = 2,

    /// <summary>The exact request window elapsed before an accepted answer selection.</summary>
    Expired = 3,

    /// <summary>The governing run cancelled the checkpoint without treating cancellation as an answer.</summary>
    Cancelled = 4,

    /// <summary>A distinct exact checkpoint superseded this checkpoint before an accepted answer selection.</summary>
    Superseded = 5,

    /// <summary>A later runner recorded terminal consumption of the already answered checkpoint.</summary>
    Terminal = 6
}
