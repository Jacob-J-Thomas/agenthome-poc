using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Triggers;
using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Common.Triggers.Models;

/// <summary>
/// Represents one bounded canonical trigger-delivery admission envelope.
/// </summary>
/// <remarks>The envelope is observation evidence only and never grants capability, permission, authority, or execution.</remarks>
public sealed record TriggerDeliveryEnvelope
{
    /// <summary>Gets the only supported experimental envelope schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    internal TriggerDeliveryEnvelope(int schemaVersion, TriggerDeliveryId deliveryId, TriggerDeduplicationId deduplicationId, TriggerKind kind, TriggerAdapterReference adapter, TriggerLoopReference loop, TriggerActorContext actorContext, TriggerAuthorityEvidence authority, TriggerTemporalEvidence temporal, TriggerPayloadEvidence payload, TriggerRedeliveryEvidence redelivery, ScheduleExecutionDirective? scheduleExecutionDirective, bool publicationRequested, CustomLoopConversationReference? invokingConversation, TriggerAdmissionStatus visibleStatus, TriggerAdmissionReason visibleReason)
    {
        SchemaVersion = schemaVersion;
        DeliveryId = deliveryId;
        DeduplicationId = deduplicationId;
        Kind = kind;
        Adapter = adapter;
        Loop = loop;
        ActorContext = actorContext;
        Authority = authority;
        Temporal = temporal;
        Payload = payload;
        Redelivery = redelivery;
        ScheduleExecutionDirective = scheduleExecutionDirective;
        PublicationRequested = publicationRequested;
        InvokingConversation = invokingConversation;
        VisibleStatus = visibleStatus;
        VisibleReason = visibleReason;
    }

    /// <summary>Gets the envelope schema version.</summary>
    public int SchemaVersion { get; }

    /// <summary>Gets the delivery identifier.</summary>
    public TriggerDeliveryId DeliveryId { get; }

    /// <summary>Gets the deduplication identity.</summary>
    public TriggerDeduplicationId DeduplicationId { get; }

    /// <summary>Gets the trigger source kind.</summary>
    public TriggerKind Kind { get; }

    /// <summary>Gets the exact adapter evidence.</summary>
    public TriggerAdapterReference Adapter { get; }

    /// <summary>Gets the exact loop evidence.</summary>
    public TriggerLoopReference Loop { get; }

    /// <summary>Gets the exact actor and scope evidence.</summary>
    public TriggerActorContext ActorContext { get; }

    /// <summary>Gets the exact authority evidence.</summary>
    public TriggerAuthorityEvidence Authority { get; }

    /// <summary>Gets the exact temporal evidence.</summary>
    public TriggerTemporalEvidence Temporal { get; }

    /// <summary>Gets the bounded payload evidence.</summary>
    public TriggerPayloadEvidence Payload { get; }

    /// <summary>Gets the redelivery evidence.</summary>
    public TriggerRedeliveryEvidence Redelivery { get; }

    /// <summary>Gets the exact schedule execution coordinates required for time-trigger deliveries.</summary>
    public ScheduleExecutionDirective? ScheduleExecutionDirective { get; }

    /// <summary>Gets a value indicating whether later execution requests conversation publication.</summary>
    public bool PublicationRequested { get; }

    /// <summary>Gets the required invoking-conversation reference for requested publication.</summary>
    public CustomLoopConversationReference? InvokingConversation { get; }

    /// <summary>Gets the caller-visible status evidence, which is never trusted as a grant.</summary>
    public TriggerAdmissionStatus VisibleStatus { get; }

    /// <summary>Gets the caller-visible reason evidence, which is never trusted as a grant.</summary>
    public TriggerAdmissionReason VisibleReason { get; }
}
