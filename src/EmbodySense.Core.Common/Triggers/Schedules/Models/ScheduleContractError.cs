namespace EmbodySense.Core.Common.Triggers.Schedules.Models;

/// <summary>Identifies one bounded structured schedule-contract validation failure.</summary>
/// <param name="Code">The stable machine-readable failure code.</param>
/// <param name="Path">The exact contract path that failed.</param>
public sealed record ScheduleContractError(string Code, string Path);
