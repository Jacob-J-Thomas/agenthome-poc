using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Models;

namespace EmbodySense.Core.Application.Triggers.Schedules.Models;

/// <summary>Captures one bounded fresh evidence snapshot and isolated resolved payload bytes.</summary>
public sealed class ScheduleCurrentEvidence
{
    private readonly byte[]? _resolvedPayload;

    /// <summary>Initializes one fresh bounded evidence snapshot.</summary>
    public ScheduleCurrentEvidence(
        string evidenceHash,
        DateTimeOffset observedAtUtc,
        TriggerLoopReference target,
        TriggerAdapterReference adapter,
        TriggerActorContext actorContext,
        TriggerAuthorityEvidence authority,
        bool recurrencePermitted,
        byte[] resolvedPayload)
    {
        EvidenceHash = evidenceHash;
        ObservedAtUtc = observedAtUtc;
        Target = target;
        Adapter = adapter;
        ActorContext = actorContext;
        Authority = authority;
        RecurrencePermitted = recurrencePermitted;
        _resolvedPayload = resolvedPayload is { Length: <= TriggerDeliveryLimits.MaxInlinePayloadBytes }
            ? resolvedPayload.ToArray()
            : null;
    }

    /// <summary>Gets the canonical lowercase SHA-256 evidence hash.</summary>
    public string EvidenceHash { get; }

    /// <summary>Gets the exact UTC instant at which this complete snapshot was resolved.</summary>
    public DateTimeOffset ObservedAtUtc { get; }

    /// <summary>Gets the exact current governed target.</summary>
    public TriggerLoopReference Target { get; }

    /// <summary>Gets the exact current adapter pin.</summary>
    public TriggerAdapterReference Adapter { get; }

    /// <summary>Gets the exact current actor and scope.</summary>
    public TriggerActorContext ActorContext { get; }

    /// <summary>Gets the fresh non-executing authority evidence.</summary>
    public TriggerAuthorityEvidence Authority { get; }

    /// <summary>Gets whether recurrence invocation is currently permitted.</summary>
    public bool RecurrencePermitted { get; }

    /// <summary>Tries to return an isolated copy of bounded resolved payload bytes.</summary>
    public bool TryGetResolvedPayload(out byte[]? payload)
    {
        payload = _resolvedPayload?.ToArray();
        return payload is not null;
    }

    /// <summary>Returns an isolated copy of resolved payload bytes.</summary>
    /// <exception cref="InvalidOperationException">The supplied port evidence did not contain a bounded payload.</exception>
    public byte[] GetResolvedPayload()
        => _resolvedPayload?.ToArray()
            ?? throw new InvalidOperationException("Current schedule evidence did not contain a bounded resolved payload.");
}
