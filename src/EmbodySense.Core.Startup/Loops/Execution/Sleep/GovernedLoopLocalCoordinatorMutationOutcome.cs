using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Startup.Loops.Execution.Sleep;

internal sealed record GovernedLoopLocalCoordinatorMutationOutcome(
    GovernedLoopLocalCoordinatorMutationStatus Status,
    GovernedLoopCoordinatorSnapshot? Snapshot)
{
    internal static GovernedLoopLocalCoordinatorMutationOutcome Success(GovernedLoopCoordinatorSnapshot snapshot)
        => new(GovernedLoopLocalCoordinatorMutationStatus.Succeeded, snapshot);

    internal static GovernedLoopLocalCoordinatorMutationOutcome OwnershipLost(GovernedLoopCoordinatorSnapshot snapshot)
        => new(GovernedLoopLocalCoordinatorMutationStatus.OwnershipLost, snapshot);

    internal static GovernedLoopLocalCoordinatorMutationOutcome Conflict(GovernedLoopCoordinatorSnapshot snapshot)
        => new(GovernedLoopLocalCoordinatorMutationStatus.Conflict, snapshot);

    internal static GovernedLoopLocalCoordinatorMutationOutcome Corrupt(GovernedLoopCoordinatorSnapshot snapshot)
        => new(GovernedLoopLocalCoordinatorMutationStatus.Corrupt, snapshot);

    internal static GovernedLoopLocalCoordinatorMutationOutcome Unavailable(GovernedLoopCoordinatorSnapshot snapshot)
        => new(GovernedLoopLocalCoordinatorMutationStatus.Unavailable, snapshot);
}
