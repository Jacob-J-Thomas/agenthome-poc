namespace EmbodySense.Core.Application.Loops.Sequential.Models;

/// <summary>Requests one exact plan-node dispatch under a guard-issued run anchor.</summary>
/// <param name="SchemaVersion">The request schema version, which must be 1.</param>
/// <param name="Anchor">The exact guard-issued immutable run anchor.</param>
/// <param name="Plan">The exact builder-issued immutable linear plan.</param>
/// <param name="Node">The exact node instance selected from the plan.</param>
/// <param name="Attempt">The positive bounded node-attempt number.</param>
public sealed record GovernedLoopSequentialNodeDispatchRequest(
    int SchemaVersion,
    GovernedLoopSequentialRunAnchor Anchor,
    GovernedLoopSequentialPlan Plan,
    GovernedLoopSequentialPlanNode Node,
    int Attempt)
{
    /// <summary>Gets the only supported experimental request schema version.</summary>
    public const int CurrentSchemaVersion = 1;
}
