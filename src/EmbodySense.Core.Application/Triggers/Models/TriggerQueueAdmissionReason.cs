namespace EmbodySense.Core.Application.Triggers.Models;

/// <summary>Defines stable reasons for trigger queue admission outcomes.</summary>
public enum TriggerQueueAdmissionReason
{
    /// <summary>The envelope was committed to the queue.</summary>
    Enqueued,

    /// <summary>The exact durable outcome already existed.</summary>
    ExactReplay,

    /// <summary>The delivery admission contract rejected the envelope.</summary>
    AdmissionRejected,

    /// <summary>A delivery or deduplication identity was reused with conflicting evidence.</summary>
    IdentityConflict,

    /// <summary>The canonical envelope exceeds the per-entry byte bound.</summary>
    EntryBytesExceeded,

    /// <summary>The active queue count bound is full.</summary>
    QueueCountExceeded,

    /// <summary>The active queue aggregate byte bound is full.</summary>
    QueueBytesExceeded,

    /// <summary>The loop's active-entry quota is full.</summary>
    LoopQuotaExceeded,

    /// <summary>The retained evidence count or byte bound is full.</summary>
    RetainedEvidenceExceeded,

    /// <summary>The authenticated cleanup-tombstone quota cannot reserve the next durable mutation.</summary>
    DurabilityTombstoneCapacityExceeded,

    /// <summary>The request forbids queueing and this component has no dispatch capability.</summary>
    ImmediateModeBusy,

    /// <summary>The delivery admission dependency was unavailable or not terminal.</summary>
    AdmissionUnavailable,

    /// <summary>The durable queue could not be read or changed safely.</summary>
    StorageUnavailable
}
