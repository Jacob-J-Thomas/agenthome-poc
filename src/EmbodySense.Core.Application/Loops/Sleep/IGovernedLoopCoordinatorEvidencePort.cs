using EmbodySense.Core.Application.Loops.Sleep.Models;

namespace EmbodySense.Core.Application.Loops.Sleep;

/// <summary>Owns the atomic durable evidence boundary for one fenced local background coordinator.</summary>
/// <remarks>
/// Persistence implementations must validate every embedded Common contract before comparing or committing it, must retain acquisition ownership, starting lifecycle, and initial heartbeat atomically,
/// and may append a successor only after either the prior exclusive heartbeat lease expires under
/// <c>GovernedLoopSleepContractValidator.ValidateHandoff</c> or the exact same owner proves its own durable stopped
/// lifecycle through <c>GovernedLoopSleepContractValidator.ValidateTerminalSameOwnerRestart</c>.
/// </remarks>
public interface IGovernedLoopCoordinatorEvidencePort
{
    /// <summary>Reads the bounded current evidence for one stable coordinator identity.</summary>
    Task<GovernedLoopCoordinatorReadResult?> ReadAsync(string coordinatorId, CancellationToken cancellationToken = default);

    /// <summary>Atomically acquires a new coordinator, a lease-expired fenced handoff, or a same-owner stopped restart.</summary>
    Task<GovernedLoopCoordinatorAcquisitionResult?> TryAcquireAsync(
        GovernedLoopCoordinatorAcquisitionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Renews one exact owner's heartbeat through an ownership and prior-heartbeat compare-and-swap.</summary>
    Task<GovernedLoopCoordinatorHeartbeatMutationResult?> RenewHeartbeatAsync(
        GovernedLoopCoordinatorHeartbeatMutationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Appends one lifecycle successor through an ownership and prior-lifecycle compare-and-swap.</summary>
    Task<GovernedLoopCoordinatorLifecycleMutationResult?> AppendLifecycleAsync(
        GovernedLoopCoordinatorLifecycleMutationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Appends one failure successor through an ownership and prior-failure compare-and-swap.</summary>
    Task<GovernedLoopCoordinatorFailureMutationResult?> AppendFailureAsync(
        GovernedLoopCoordinatorFailureMutationRequest request,
        CancellationToken cancellationToken = default);
}
