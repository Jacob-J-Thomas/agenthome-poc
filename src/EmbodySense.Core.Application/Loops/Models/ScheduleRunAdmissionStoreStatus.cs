namespace EmbodySense.Core.Application.Loops.Models;

/// <summary>Identifies the closed result of atomic schedule-aware run creation.</summary>
public enum ScheduleRunAdmissionStoreStatus
{
    /// <summary>No supported result was produced.</summary>
    Unknown = 0,

    /// <summary>The exact occurrence created its canonical run.</summary>
    Created = 1,

    /// <summary>The exact occurrence already owns a canonical run and no creation was repeated.</summary>
    Replayed = 2,

    /// <summary>The Skip policy durably terminalized the occurrence behind another run.</summary>
    OverlapSkipped = 3,

    /// <summary>The DeferOne policy durably retained the occurrence for later reselection.</summary>
    OverlapDeferred = 4,

    /// <summary>The Allow policy durably serialized the occurrence for later reselection.</summary>
    OverlapSerialized = 5,

    /// <summary>Another exact DeferOne occurrence already owns the single deferred slot.</summary>
    DeferredOneSuppressed = 6,

    /// <summary>Existing durable evidence is bound to different immutable coordinates.</summary>
    Conflict = 7,

    /// <summary>A bounded run or schedule-admission evidence limit rejected creation.</summary>
    LimitExceeded = 8,

    /// <summary>The store cannot safely perform schedule-aware atomic admission.</summary>
    Unavailable = 9,
}
