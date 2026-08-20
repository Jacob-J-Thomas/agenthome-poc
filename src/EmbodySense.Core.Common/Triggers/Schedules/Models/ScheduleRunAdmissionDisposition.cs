namespace EmbodySense.Core.Common.Triggers.Schedules.Models;

/// <summary>Identifies one run-store-owned disposition for an authenticated schedule occurrence.</summary>
public enum ScheduleRunAdmissionDisposition
{
    /// <summary>No supported disposition exists.</summary>
    Unknown = 0,

    /// <summary>The exact occurrence owns a newly materialized canonical run.</summary>
    RunCreated = 1,

    /// <summary>The Skip policy terminalized the exact occurrence behind an existing run.</summary>
    OverlapSkipped = 2,

    /// <summary>The DeferOne policy retained the exact occurrence for later reselection.</summary>
    OverlapDeferred = 3,

    /// <summary>The Allow policy retained the exact occurrence while preserving serialized execution.</summary>
    OverlapSerialized = 4,

    /// <summary>A prior exact DeferOne occurrence already owns the single deferred slot.</summary>
    DeferredOneSuppressed = 5,
}
