namespace EmbodySense.Core.Persistence.Triggers.Schedules;

/// <summary>Signals that otherwise structured schedule persistence exceeded an explicit retained bound.</summary>
internal sealed class ScheduleStoreCodecLimitException : Exception
{
}
