namespace EmbodySense.Core.Application.Triggers.Models;

/// <summary>Defines whether a selected trigger may cross the durable dispatch-intent boundary.</summary>
public enum TriggerWorkerDispatchReadinessStatus
{
    /// <summary>No recognized decision was returned.</summary>
    Unknown = 0,
    /// <summary>The selected trigger may continue to current-evidence authorization and durable intent.</summary>
    Ready = 1,
    /// <summary>The exact schedule delivery exists but must await terminal provenance finalization.</summary>
    RetryAfterScheduleFinalization = 2,
    /// <summary>Readiness could not be proved; persist bounded attention without invoking a provider.</summary>
    RequiresAttention = 3,
}
