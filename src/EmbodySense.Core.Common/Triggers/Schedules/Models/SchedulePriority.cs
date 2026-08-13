namespace EmbodySense.Core.Common.Triggers.Schedules.Models;

/// <summary>Defines the bounded caller-requested queue priority carried by a schedule.</summary>
public enum SchedulePriority
{
    /// <summary>The priority is absent or unsupported.</summary>
    Unknown = 0,
    /// <summary>Background priority.</summary>
    Background = 1,
    /// <summary>Normal priority.</summary>
    Normal = 2,
    /// <summary>Elevated priority.</summary>
    Elevated = 3,
    /// <summary>Critical priority.</summary>
    Critical = 4,
}
