namespace EmbodySense.Core.Common.Triggers.Schedules;

internal static class ScheduleCollectionSnapshot
{
    internal static IReadOnlyList<T>? CopyAndOrder<T>(IReadOnlyList<T>? source, int maximum, IComparer<T> comparer)
    {
        if (source is null)
        {
            return null;
        }

        var count = source.Count < 0 ? maximum + 1 : Math.Min(source.Count, maximum + 1);
        var snapshot = new T[count];
        for (var index = 0; index < count; index++)
        {
            snapshot[index] = source[index];
        }

        Array.Sort(snapshot, comparer);
        return Array.AsReadOnly(snapshot);
    }
}
