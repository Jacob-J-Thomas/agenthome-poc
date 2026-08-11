namespace EmbodySense.Core.Application.Loops.GraphAuthoring.Models;

/// <summary>Identifies whether a durable full authoring intent awaits or has terminal lifecycle evidence.</summary>
public enum GovernedLoopGraphRevisionOperationState
{
    /// <summary>No trustworthy state was supplied.</summary>
    Unknown = 0,
    /// <summary>The exact full intent is durable and may be safely continued.</summary>
    Pending = 1,
    /// <summary>The full intent has exact terminal lifecycle evidence.</summary>
    Terminal = 2,
}
