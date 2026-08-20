namespace EmbodySense.Core.Common.Triggers.Schedules.Models;

/// <summary>Defines policy when an exact governed run overlaps an occurrence.</summary>
public enum ScheduleOverlapPolicy
{
    /// <summary>The policy is absent or unsupported.</summary>
    Unknown = 0,
    /// <summary>Allow the occurrence despite the exact active-run evidence.</summary>
    Allow = 1,
    /// <summary>Skip the occurrence with durable overlap evidence.</summary>
    Skip = 2,
    /// <summary>Retain one deferred occurrence without multiplying deliveries.</summary>
    DeferOne = 3,
}
