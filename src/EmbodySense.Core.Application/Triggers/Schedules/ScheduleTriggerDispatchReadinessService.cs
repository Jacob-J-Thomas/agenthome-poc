using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Application.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Triggers.Models;

namespace EmbodySense.Core.Application.Triggers.Schedules;

/// <summary>Defers an exact selected schedule delivery until its accepted provenance becomes terminal.</summary>
public sealed class ScheduleTriggerDispatchReadinessService : ITriggerWorkerDispatchReadinessPort
{
    private const string ScheduleCapabilityId = "org.embodysense/triggers/time";
    private const string ScheduleImplementationId = "triggers/time";
    private const string ScheduleProviderId = "org.embodysense";
    private readonly IScheduleDeliveryProvenancePort _provenance;

    /// <summary>Initializes the pre-intent check over the authoritative schedule evidence source.</summary>
    public ScheduleTriggerDispatchReadinessService(IScheduleDeliveryProvenancePort provenance)
    {
        _provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
    }

    /// <inheritdoc />
    public async Task<TriggerWorkerDispatchReadinessResult> CheckAsync(
        TriggerDeliveryEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Kind != TriggerKind.Time
            || envelope.Loop.Kind != TriggerLoopTargetKind.GovernedPublication
            || !string.Equals(envelope.Adapter.Capability.Id.Value, ScheduleCapabilityId, StringComparison.Ordinal)
            || !string.Equals(envelope.Adapter.Implementation.ProviderId.Value, ScheduleProviderId, StringComparison.Ordinal)
            || !string.Equals(envelope.Adapter.Implementation.ImplementationId, ScheduleImplementationId, StringComparison.Ordinal))
        {
            return new TriggerWorkerDispatchReadinessResult(TriggerWorkerDispatchReadinessStatus.Ready);
        }

        var result = await _provenance.ResolveAsync(envelope, cancellationToken).ConfigureAwait(false);
        var status = result?.Status switch
        {
            ScheduleDeliveryProvenanceStatus.PendingFinalization
                => TriggerWorkerDispatchReadinessStatus.RetryAfterScheduleFinalization,
            ScheduleDeliveryProvenanceStatus.Found
                or ScheduleDeliveryProvenanceStatus.NotFound
                or ScheduleDeliveryProvenanceStatus.Conflict
                => TriggerWorkerDispatchReadinessStatus.Ready,
            _ => TriggerWorkerDispatchReadinessStatus.RequiresAttention,
        };
        return new TriggerWorkerDispatchReadinessResult(status);
    }
}
