using EmbodySense.Core.Application.Loops.Sleep.Models;

namespace EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

internal sealed record GovernedLoopLocalCoordinatorSessionOutcome(
    GovernedLoopLocalCoordinatorStopStatus Status,
    GovernedLoopCoordinatorSnapshot Snapshot);
