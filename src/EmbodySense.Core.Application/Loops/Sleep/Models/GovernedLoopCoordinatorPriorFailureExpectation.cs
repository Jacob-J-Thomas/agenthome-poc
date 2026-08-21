namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>States the exact prior failure head expected before one append.</summary>
public enum GovernedLoopCoordinatorPriorFailureExpectation
{
    /// <summary>No failure exists for the expected ownership.</summary>
    None = 1,

    /// <summary>An exact prior failure sequence and hash exist.</summary>
    Existing = 2
}
