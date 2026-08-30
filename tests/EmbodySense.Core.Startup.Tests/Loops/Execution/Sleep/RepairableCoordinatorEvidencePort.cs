using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Common.Loops.Execution.Sleep;
using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

internal sealed class RepairableCoordinatorEvidencePort : IGovernedLoopCoordinatorEvidencePort, IGovernedLoopCoordinatorRepairPort
{
    private readonly Lock _gate = new();
    private readonly RecordingCoordinatorEvidencePort _inner = new();
    private readonly List<GovernedLoopCoordinatorRepairDisposition> _repairs = [];

    internal int RepairAcquisitionCalls { get; private set; }

    internal List<GovernedLoopCoordinatorFailure> Failures => _inner.Failures;

    internal GovernedLoopCoordinatorSnapshot? Snapshot => _inner.Snapshot;

    public Task<GovernedLoopCoordinatorReadResult?> ReadAsync(string coordinatorId, CancellationToken cancellationToken = default)
        => _inner.ReadAsync(coordinatorId, cancellationToken);

    public Task<GovernedLoopCoordinatorAcquisitionResult?> TryAcquireAsync(
        GovernedLoopCoordinatorAcquisitionRequest request,
        CancellationToken cancellationToken = default)
        => _inner.TryAcquireAsync(request, cancellationToken);

    public Task<GovernedLoopCoordinatorHeartbeatMutationResult?> RenewHeartbeatAsync(
        GovernedLoopCoordinatorHeartbeatMutationRequest request,
        CancellationToken cancellationToken = default)
        => _inner.RenewHeartbeatAsync(request, cancellationToken);

    public Task<GovernedLoopCoordinatorLifecycleMutationResult?> AppendLifecycleAsync(
        GovernedLoopCoordinatorLifecycleMutationRequest request,
        CancellationToken cancellationToken = default)
        => _inner.AppendLifecycleAsync(request, cancellationToken);

    public Task<GovernedLoopCoordinatorFailureMutationResult?> AppendFailureAsync(
        GovernedLoopCoordinatorFailureMutationRequest request,
        CancellationToken cancellationToken = default)
        => _inner.AppendFailureAsync(request, cancellationToken);

    public Task<GovernedLoopCoordinatorRepairReadResult?> ReadAsync(
        string coordinatorId,
        string failedOwnershipHash,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var repair = _repairs.LastOrDefault(item => string.Equals(item.CoordinatorId, coordinatorId, StringComparison.Ordinal)
                && string.Equals(item.FailedOwnership.ContentHash, failedOwnershipHash, StringComparison.Ordinal));
            return Task.FromResult<GovernedLoopCoordinatorRepairReadResult?>(repair is null
                ? new GovernedLoopCoordinatorRepairReadResult(GovernedLoopCoordinatorRepairReadStatus.NotFound)
                : new GovernedLoopCoordinatorRepairReadResult(GovernedLoopCoordinatorRepairReadStatus.Found, repair));
        }
    }

    public Task<GovernedLoopCoordinatorRepairMutationResult?> AppendAsync(
        GovernedLoopCoordinatorRepairDisposition disposition,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!GovernedLoopSleepContractValidator.Validate(disposition).IsValid)
        {
            return Task.FromResult<GovernedLoopCoordinatorRepairMutationResult?>(
                new GovernedLoopCoordinatorRepairMutationResult(GovernedLoopCoordinatorRepairMutationStatus.Corrupt));
        }

        lock (_gate)
        {
            var existing = _repairs.SingleOrDefault(item => string.Equals(item.OperationId, disposition.OperationId, StringComparison.Ordinal));
            if (existing is not null)
            {
                return Task.FromResult<GovernedLoopCoordinatorRepairMutationResult?>(new GovernedLoopCoordinatorRepairMutationResult(
                    existing == disposition ? GovernedLoopCoordinatorRepairMutationStatus.Duplicate : GovernedLoopCoordinatorRepairMutationStatus.Conflict,
                    existing == disposition ? existing : null));
            }

            _repairs.Add(disposition);
            return Task.FromResult<GovernedLoopCoordinatorRepairMutationResult?>(
                new GovernedLoopCoordinatorRepairMutationResult(GovernedLoopCoordinatorRepairMutationStatus.Appended, disposition));
        }
    }

    public async Task<GovernedLoopCoordinatorAcquisitionResult?> TryAcquireAfterRepairAsync(
        GovernedLoopCoordinatorRepairAcquisitionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!GovernedLoopCoordinatorEvidenceContract.IsValid(request))
        {
            return new GovernedLoopCoordinatorAcquisitionResult(GovernedLoopCoordinatorAcquisitionStatus.Corrupt);
        }

        lock (_gate)
        {
            if (!_repairs.Contains(request.Repair))
            {
                return new GovernedLoopCoordinatorAcquisitionResult(GovernedLoopCoordinatorAcquisitionStatus.Conflict, Snapshot);
            }

            RepairAcquisitionCalls++;
        }

        return await _inner.TryAcquireAfterRepairAsync(request.Acquisition, cancellationToken);
    }
}
