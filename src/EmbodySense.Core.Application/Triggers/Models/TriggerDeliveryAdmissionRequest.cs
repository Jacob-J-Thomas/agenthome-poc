using EmbodySense.Core.Common.Triggers.Models;

namespace EmbodySense.Core.Application.Triggers.Models;

/// <summary>
/// Requests fail-closed evaluation of one envelope against exact current evidence.
/// </summary>
/// <remarks>The request contains no execution, dispatch, persistence, queue, scheduling, approval, or ambient-catalog capability.</remarks>
public sealed record TriggerDeliveryAdmissionRequest
{
    internal TriggerDeliveryAdmissionRequest(TriggerDeliveryEnvelope envelope, TriggerLoopReference currentLoop, TriggerAdapterReference currentAdapter, bool isAdapterAvailable, TriggerActorContext currentActorContext, TriggerAuthorityEvidence currentAuthority, DateTimeOffset evaluatedAtUtc)
    {
        Envelope = envelope;
        CurrentLoop = currentLoop;
        CurrentAdapter = currentAdapter;
        IsAdapterAvailable = isAdapterAvailable;
        CurrentActorContext = currentActorContext;
        CurrentAuthority = currentAuthority;
        EvaluatedAtUtc = evaluatedAtUtc;
    }

    /// <summary>Gets the untrusted delivery evidence to evaluate.</summary>
    public TriggerDeliveryEnvelope Envelope { get; }

    /// <summary>Gets the exact current loop revision and hash.</summary>
    public TriggerLoopReference CurrentLoop { get; }

    /// <summary>Gets the exact current adapter capability and implementation pin.</summary>
    public TriggerAdapterReference CurrentAdapter { get; }

    /// <summary>Gets a value indicating whether the exact pinned adapter is currently available.</summary>
    public bool IsAdapterAvailable { get; }

    /// <summary>Gets the exact current actor, surface, workspace, and role.</summary>
    public TriggerActorContext CurrentActorContext { get; }

    /// <summary>Gets the exact current non-executing authority evidence.</summary>
    public TriggerAuthorityEvidence CurrentAuthority { get; }

    /// <summary>Gets the caller-supplied UTC evaluation instant.</summary>
    public DateTimeOffset EvaluatedAtUtc { get; }
}
