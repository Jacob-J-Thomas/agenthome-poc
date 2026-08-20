namespace EmbodySense.Core.Application.Loops.Sequential.Models;

/// <summary>Defines the closed ready-node tie-breaking rule for one immutable topology plan.</summary>
public enum GovernedLoopTopologyReadyOrdering
{
    /// <summary>No supported order is declared.</summary>
    Unknown = 0,
    /// <summary>Order first by stable plan ordinal and then exact node identity.</summary>
    StaticOrdinalThenNodeId,
}
