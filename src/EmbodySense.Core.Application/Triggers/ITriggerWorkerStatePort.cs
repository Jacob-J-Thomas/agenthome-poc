using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Common.Triggers;

namespace EmbodySense.Core.Application.Triggers;

/// <summary>Defines atomic durable selection, ownership, and dispatch-state transitions.</summary>
public interface ITriggerWorkerStatePort
{
    /// <summary>Selects and owns one eligible entry from an exact queue generation.</summary>
    /// <param name="request">The bounded generation, clock, fairness, worker, and lease inputs.</param>
    /// <param name="cancellationToken">A token honored before the ownership commit.</param>
    /// <returns>The closed selection posture and selected evidence when acquired.</returns>
    Task<TriggerWorkerSelectionResult> SelectAsync(TriggerWorkerSelectionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Renews exact live ownership without changing its generation.</summary>
    /// <param name="deliveryId">The exact delivery identity.</param>
    /// <param name="workerId">The exact current worker identity.</param>
    /// <param name="leaseGeneration">The exact current ownership generation.</param>
    /// <param name="expectedRevision">The exact entry revision.</param>
    /// <param name="renewedAtUtc">The monotonic UTC renewal instant.</param>
    /// <param name="leaseDuration">The bounded replacement lease duration.</param>
    /// <param name="cancellationToken">A token honored before the renewal commit.</param>
    /// <returns>The closed mutation posture and latest entry evidence.</returns>
    Task<TriggerWorkerMutationResult> RenewAsync(TriggerDeliveryId deliveryId, string workerId, long leaseGeneration, long expectedRevision, DateTimeOffset renewedAtUtc, TimeSpan leaseDuration, CancellationToken cancellationToken = default);

    /// <summary>Releases exact ownership before dispatch intent and returns the entry to deterministic selection.</summary>
    /// <param name="deliveryId">The exact delivery identity.</param>
    /// <param name="workerId">The exact current worker identity.</param>
    /// <param name="leaseGeneration">The exact current ownership generation.</param>
    /// <param name="expectedRevision">The exact entry revision.</param>
    /// <param name="releasedAtUtc">The monotonic UTC release instant.</param>
    /// <param name="cancellationToken">A token honored before the release commit.</param>
    /// <returns>The closed mutation posture and latest entry evidence.</returns>
    Task<TriggerWorkerMutationResult> ReleaseAsync(TriggerDeliveryId deliveryId, string workerId, long leaseGeneration, long expectedRevision, DateTimeOffset releasedAtUtc, CancellationToken cancellationToken = default);

    /// <summary>Persists exact authorization proof and dispatch intent before invoking a governed runner.</summary>
    /// <param name="deliveryId">The exact delivery identity.</param>
    /// <param name="workerId">The exact current worker identity.</param>
    /// <param name="leaseGeneration">The exact current ownership generation.</param>
    /// <param name="expectedRevision">The exact entry revision.</param>
    /// <param name="intent">The exact request, ownership, and current-authority binding.</param>
    /// <param name="cancellationToken">A token honored before the intent commit.</param>
    /// <returns>The closed mutation posture and latest entry evidence.</returns>
    Task<TriggerWorkerMutationResult> BeginDispatchAsync(TriggerDeliveryId deliveryId, string workerId, long leaseGeneration, long expectedRevision, TriggerDispatchEvidence intent, CancellationToken cancellationToken = default);

    /// <summary>Persists a proved rejection before provider dispatch.</summary>
    /// <param name="deliveryId">The exact delivery identity.</param>
    /// <param name="workerId">The exact current worker identity.</param>
    /// <param name="leaseGeneration">The exact current ownership generation.</param>
    /// <param name="expectedRevision">The exact entry revision.</param>
    /// <param name="rejection">The exact pre-dispatch rejection evidence.</param>
    /// <param name="cancellationToken">A token honored before the rejection commit.</param>
    /// <returns>The closed mutation posture and latest entry evidence.</returns>
    Task<TriggerWorkerMutationResult> RejectBeforeDispatchAsync(TriggerDeliveryId deliveryId, string workerId, long leaseGeneration, long expectedRevision, TriggerDispatchEvidence rejection, CancellationToken cancellationToken = default);

    /// <summary>Persists the exact accepted, rejected, or ambiguous outcome for prior durable intent.</summary>
    /// <param name="deliveryId">The exact delivery identity.</param>
    /// <param name="workerId">The exact current worker identity.</param>
    /// <param name="leaseGeneration">The exact current ownership generation.</param>
    /// <param name="expectedRevision">The exact entry revision containing durable intent.</param>
    /// <param name="outcome">The exact intent-bound provider outcome evidence.</param>
    /// <param name="cancellationToken">A token honored before the outcome commit.</param>
    /// <returns>The closed mutation posture and latest entry evidence. Proved non-ambiguous outcomes require live ownership at their recorded time and the store's under-lock trusted observation; an exact needs-review outcome may close ambiguity after expiry.</returns>
    Task<TriggerWorkerMutationResult> CompleteDispatchAsync(TriggerDeliveryId deliveryId, string workerId, long leaseGeneration, long expectedRevision, TriggerDispatchEvidence outcome, CancellationToken cancellationToken = default);
}
