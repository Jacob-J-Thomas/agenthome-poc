using EmbodySense.Core.Application.Loops.Sleep.Models;

namespace EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

/// <summary>Reports one explicit local coordinator stop request.</summary>
/// <param name="Status">The closed stop outcome.</param>
/// <param name="Snapshot">The latest validated durable evidence when safely readable.</param>
public sealed record GovernedLoopLocalCoordinatorStopResult(
    GovernedLoopLocalCoordinatorStopStatus Status,
    GovernedLoopCoordinatorSnapshot? Snapshot = null);
