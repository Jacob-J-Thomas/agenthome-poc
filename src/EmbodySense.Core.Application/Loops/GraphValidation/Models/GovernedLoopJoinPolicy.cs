namespace EmbodySense.Core.Application.Loops.GraphValidation.Models;

/// <summary>Declares the catalog-owned control-arrival rule for an exact join descriptor.</summary>
public enum GovernedLoopJoinPolicy
{
    /// <summary>The descriptor is not a join.</summary>
    None = 0,
    /// <summary>Any one incoming control path satisfies the join.</summary>
    Any,
    /// <summary>All declared incoming control paths must be jointly satisfiable.</summary>
    All
}
