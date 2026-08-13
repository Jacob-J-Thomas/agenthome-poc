namespace EmbodySense.Core.Persistence.Triggers.Schedules;

/// <summary>Preserves an intentionally ambiguous crash-boundary observer failure.</summary>
internal sealed class ScheduleStoreBoundaryObserverException : Exception
{
    public ScheduleStoreBoundaryObserverException(Exception innerException)
        : base("The schedule persistence boundary observer interrupted the operation.", innerException)
    {
    }
}
