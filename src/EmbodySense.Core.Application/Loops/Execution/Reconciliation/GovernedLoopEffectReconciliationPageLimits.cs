namespace EmbodySense.Core.Application.Loops.Execution.Reconciliation;

internal static class GovernedLoopEffectReconciliationPageLimits
{
    internal const int MaximumPageSize = 100;
    internal const int MaximumCursorLength = 1024;

    internal static int RequirePageSize(int maximumCount, string parameterName)
    {
        return maximumCount is >= 1 and <= MaximumPageSize
            ? maximumCount
            : throw new ArgumentOutOfRangeException(parameterName, $"A reconciliation page must contain between 1 and {MaximumPageSize} entries.");
    }

    internal static string? CaptureCursor(string? cursor, string parameterName)
    {
        if (cursor?.Length > MaximumCursorLength)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"A reconciliation cursor cannot exceed {MaximumCursorLength} characters.");
        }

        return cursor;
    }

    internal static IReadOnlyList<T> CapturePage<T>(IReadOnlyList<T> values, Func<T, T> copy, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        ArgumentNullException.ThrowIfNull(copy);
        var declaredCount = values.Count;
        if (declaredCount is < 0 or > MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"A reconciliation page cannot contain more than {MaximumPageSize} entries.");
        }

        var captured = values.Take(MaximumPageSize + 1).ToArray();
        if (captured.Length != declaredCount || captured.Any(value => value is null))
        {
            throw new ArgumentException("A reconciliation page must expose one coherent, non-null snapshot.", parameterName);
        }

        return Array.AsReadOnly(captured.Select(copy).ToArray());
    }
}
