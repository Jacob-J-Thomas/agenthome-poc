namespace EmbodySense.Core.Application.Loops.Sequential.Models;

/// <summary>Identifies whether a pure canonical frontier transition was admitted.</summary>
public enum GovernedLoopSequentialFrontierTransitionStatus
{
    /// <summary>The transition request is invalid or conflicts with the committed frontier.</summary>
    Invalid = 0,

    /// <summary>The exact successor frontier was produced.</summary>
    Applied
}
