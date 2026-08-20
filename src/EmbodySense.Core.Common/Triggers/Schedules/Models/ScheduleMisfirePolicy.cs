namespace EmbodySense.Core.Common.Triggers.Schedules.Models;

/// <summary>Defines one closed misfire policy with an explicit catch-up bound.</summary>
/// <param name="Kind">The closed misfire behavior.</param>
/// <param name="CatchUpLimit">A positive bound required only for <see cref="ScheduleMisfirePolicyKind.CatchUp"/>; otherwise zero.</param>
public sealed record ScheduleMisfirePolicy(ScheduleMisfirePolicyKind Kind, int CatchUpLimit);
