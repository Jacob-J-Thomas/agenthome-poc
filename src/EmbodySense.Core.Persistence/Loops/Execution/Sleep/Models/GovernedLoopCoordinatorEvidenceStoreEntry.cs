using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Persistence.Loops.Execution.Sleep.Models;

internal sealed record GovernedLoopCoordinatorEvidenceStoreEntry(
    string CoordinatorId,
    IReadOnlyList<GovernedLoopCoordinatorOwnership> Ownerships,
    IReadOnlyList<GovernedLoopCoordinatorLifecycle> Lifecycles,
    IReadOnlyList<GovernedLoopCoordinatorHeartbeat> Heartbeats,
    IReadOnlyList<GovernedLoopCoordinatorFailure> Failures);
