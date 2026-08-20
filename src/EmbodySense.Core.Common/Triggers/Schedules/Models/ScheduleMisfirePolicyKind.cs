namespace EmbodySense.Core.Common.Triggers.Schedules.Models;

/// <summary>Defines the closed misfire policy catalog.</summary>
public enum ScheduleMisfirePolicyKind
{
    /// <summary>The policy is absent or unsupported.</summary>
    Unknown = 0,
    /// <summary>Skip every missed occurrence with bounded evidence.</summary>
    Skip = 1,
    /// <summary>Deliver only the latest missed occurrence once.</summary>
    FireLatestOnce = 2,
    /// <summary>Deliver missed occurrences up to an explicit bounded limit.</summary>
    CatchUp = 3,
}
