namespace EmbodySense.Core.Application.Loops.Sequential.Models;

/// <summary>Describes the durable disposition of one append-once sequential audit operation.</summary>
public enum GovernedLoopSequentialAuditRecordStatus
{
    /// <summary>The exact operation and audit event were recorded durably.</summary>
    Recorded = 0,

    /// <summary>The exact operation, evidence identity, and audit event were already durable.</summary>
    AlreadyRecorded = 1,

    /// <summary>The operation identifier is durably bound to different evidence or audit content.</summary>
    Conflict = 2,

    /// <summary>Durability could not be proved without ambiguity.</summary>
    Unavailable = 3,
}
