namespace EmbodySense.Core.Startup.Loops.Schedules.Models;

/// <summary>Projects one exact time-zone identifier from the server-owned schedule rules snapshot.</summary>
/// <param name="Id">The case-sensitive identifier accepted by the composing server.</param>
public sealed record GovernedLoopScheduleTimeZoneOption(string Id);
