namespace EmbodySense.Core.Common.Loops.HumanInput.Checkpoints.Models;

/// <summary>Classifies reuse of a retained durable checkpoint or evidence identity.</summary>
public enum GovernedLoopHumanInputWaitingCheckpointReplayDisposition
{
    /// <summary>The proposed identity is not retained by the compared artifact.</summary>
    New = 0,

    /// <summary>The proposed artifact is an exact canonical replay.</summary>
    ExactReplay = 1,

    /// <summary>The proposed artifact reuses an identity with divergent canonical contents.</summary>
    DivergentReuse = 2,

    /// <summary>At least one compared artifact is semantically invalid even when its self-hash is internally consistent.</summary>
    Invalid = 3
}
