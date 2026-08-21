using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Posture;

internal sealed class StubOperationalCoordinatorPort : IGovernedLoopCoordinatorEvidencePort
{
    internal GovernedLoopCoordinatorReadResult Result { get; set; } = new(GovernedLoopCoordinatorReadStatus.NotFound);

    public Task<GovernedLoopCoordinatorReadResult?> ReadAsync(string coordinatorId, CancellationToken cancellationToken = default)
        => Task.FromResult<GovernedLoopCoordinatorReadResult?>(Result);

    public Task<GovernedLoopCoordinatorAcquisitionResult?> TryAcquireAsync(GovernedLoopCoordinatorAcquisitionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<GovernedLoopCoordinatorHeartbeatMutationResult?> RenewHeartbeatAsync(GovernedLoopCoordinatorHeartbeatMutationRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<GovernedLoopCoordinatorLifecycleMutationResult?> AppendLifecycleAsync(GovernedLoopCoordinatorLifecycleMutationRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<GovernedLoopCoordinatorFailureMutationResult?> AppendFailureAsync(GovernedLoopCoordinatorFailureMutationRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
