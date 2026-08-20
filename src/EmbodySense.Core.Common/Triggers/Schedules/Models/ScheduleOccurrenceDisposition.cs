namespace EmbodySense.Core.Common.Triggers.Schedules.Models;

/// <summary>Defines why an occurrence was skipped or deferred.</summary>
public enum ScheduleOccurrenceDisposition
{
    /// <summary>The disposition is absent or unsupported.</summary>
    Unknown = 0,
    /// <summary>An invalid local wall-clock occurrence was skipped.</summary>
    InvalidLocalTimeSkipped = 1,
    /// <summary>A missed occurrence was skipped by misfire policy.</summary>
    MisfireSkipped = 2,
    /// <summary>An occurrence was skipped because exact overlap evidence was active.</summary>
    OverlapSkipped = 3,
    /// <summary>One occurrence was deferred because exact overlap evidence was active.</summary>
    OverlapDeferred = 4,
}
