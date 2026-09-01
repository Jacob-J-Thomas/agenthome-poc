namespace EmbodySense.Core.Startup.Loops.Schedules.Models;

/// <summary>Returns the bounded server-owned time-zone choices available for schedule authoring.</summary>
/// <param name="Status">The closed lowercase catalog outcome token.</param>
/// <param name="Detail">A bounded non-sensitive explanation of the catalog posture.</param>
/// <param name="TimeZones">The exact server-supported identifiers when the catalog is available.</param>
public sealed record GovernedLoopScheduleTimeZoneCatalogResponse(
    string Status,
    string Detail,
    IReadOnlyList<GovernedLoopScheduleTimeZoneOption> TimeZones)
{
    /// <summary>Gets a detached read-only view of the server-owned time-zone choices.</summary>
    public IReadOnlyList<GovernedLoopScheduleTimeZoneOption> TimeZones { get; } = Array.AsReadOnly((TimeZones ?? []).ToArray());
}
