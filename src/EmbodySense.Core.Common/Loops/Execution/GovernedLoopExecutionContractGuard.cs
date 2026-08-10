using EmbodySense.Core.Common.Loops.Custom;

namespace EmbodySense.Core.Common.Loops.Execution;

internal static class GovernedLoopExecutionContractGuard
{
    internal static void RequireSchema(int schemaVersion, string parameterName)
    {
        if (schemaVersion != GovernedLoopExecutionLimits.CurrentSchemaVersion)
        {
            throw new ArgumentException($"Governed-loop execution schema version must be {GovernedLoopExecutionLimits.CurrentSchemaVersion}.", parameterName);
        }
    }

    internal static string RequireIdentifier(string? value, string parameterName, int maxCharacters = GovernedLoopExecutionLimits.MaxIdentifierCharacters)
    {
        if (!CustomLoopArtifactIdentifier.IsValid(value, maxCharacters))
        {
            throw new ArgumentException("Governed-loop execution identifiers must be bounded canonical lowercase tokens.", parameterName);
        }

        return value!;
    }

    internal static string? RequireOptionalIdentifier(string? value, string parameterName, int maxCharacters = GovernedLoopExecutionLimits.MaxIdentifierCharacters)
    {
        return value is null ? null : RequireIdentifier(value, parameterName, maxCharacters);
    }

    internal static string RequireSha256(string? value, string parameterName)
    {
        if (value?.Length != GovernedLoopExecutionLimits.Sha256HexCharacters || value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("Governed-loop execution hashes must be canonical lowercase SHA-256 values.", parameterName);
        }

        return value;
    }

    internal static long RequirePositiveVersion(long value, string parameterName, long maximum = GovernedLoopExecutionLimits.MaxVersion)
    {
        if (value is < 1 || value > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"Governed-loop execution versions must be positive and no greater than {maximum}.");
        }

        return value;
    }

    internal static int? RequireOptionalAttempt(int? value, string parameterName)
    {
        if (value is < 1 || value > GovernedLoopExecutionLimits.MaxNodeAttempt)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"Governed-loop node attempts must be positive and no greater than {GovernedLoopExecutionLimits.MaxNodeAttempt}.");
        }

        return value;
    }

    internal static DateTimeOffset RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Governed-loop execution timestamps must be non-default UTC values with zero offset.", parameterName);
        }

        return value;
    }

    internal static IReadOnlyList<string> SnapshotSortedUniqueIdentifiers(IEnumerable<string>? values, string parameterName, int maximumCount)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        var snapshot = values.Take(maximumCount + 1).ToArray();
        if (snapshot.Length > maximumCount)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"Governed-loop execution identifier collections may contain at most {maximumCount} items.");
        }

        string? previous = null;
        foreach (var value in snapshot)
        {
            RequireIdentifier(value, parameterName);
            if (previous is not null && string.CompareOrdinal(previous, value) >= 0)
            {
                throw new ArgumentException("Governed-loop execution identifier collections must be sorted and unique.", parameterName);
            }

            previous = value;
        }

        return Array.AsReadOnly(snapshot);
    }

    internal static IReadOnlyList<TValue> SnapshotBounded<TValue>(IEnumerable<TValue>? values, string parameterName, int maximumCount)
        where TValue : class
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        var snapshot = values.Take(maximumCount + 1).ToArray();
        if (snapshot.Length > maximumCount)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"Governed-loop execution evidence collections may contain at most {maximumCount} items.");
        }

        if (snapshot.Any(item => item is null))
        {
            throw new ArgumentException("Governed-loop execution evidence collections cannot contain null items.", parameterName);
        }

        return Array.AsReadOnly(snapshot);
    }
}
