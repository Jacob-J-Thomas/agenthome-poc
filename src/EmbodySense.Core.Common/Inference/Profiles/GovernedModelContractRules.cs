using System.Globalization;
using System.Text;
using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Common.Inference.Profiles;

internal static class GovernedModelContractRules
{
    internal static void RequireSchema(int schemaVersion, string parameterName)
    {
        if (schemaVersion != GovernedModelContractLimits.CurrentSchemaVersion)
        {
            throw new ArgumentException($"Schema version must be {GovernedModelContractLimits.CurrentSchemaVersion}; compatibility translation is not supported.", parameterName);
        }
    }

    internal static string RequireIdentifier(string? value, string parameterName)
    {
        if (string.IsNullOrEmpty(value)
            || value.Length > GovernedModelContractLimits.MaxIdentifierCharacters
            || !value.IsNormalized(NormalizationForm.FormC)
            || value[0] is not (>= 'a' and <= 'z' or >= '0' and <= '9')
            || value.Any(character => character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.' or '/')))
        {
            throw new ArgumentException("Identifiers must be bounded, normalized, lowercase ASCII tokens.", parameterName);
        }

        return value;
    }

    internal static string RequirePurpose(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !CapabilityTextRules.IsSafeNormalized(value, GovernedModelContractLimits.MaxPurposeCharacters, allowEmpty: false)
            || value.Any(character => char.IsControl(character) || character is '\u2028' or '\u2029'))
        {
            throw new ArgumentException("Public purpose text must be non-empty, bounded, normalized, and free of unsafe Unicode.", parameterName);
        }

        return value;
    }

    internal static string RequireHash(string? value, string parameterName)
    {
        if (value is not { Length: GovernedModelContractLimits.Sha256Characters }
            || value.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException("Hashes must be canonical lowercase SHA-256 hexadecimal digests.", parameterName);
        }

        return value;
    }

    internal static string RequireCurrency(string? value, string parameterName)
    {
        if (value is not { Length: 3 } || value.Any(character => character is not (>= 'A' and <= 'Z')))
        {
            throw new ArgumentException("Currency must be an uppercase three-letter ISO-4217 token.", parameterName);
        }

        return value;
    }

    internal static long RequireQuantity(long value, long maximum, string parameterName, bool positive = false)
    {
        if (value < (positive ? 1 : 0) || value > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, $"The value must be between {(positive ? 1 : 0).ToString(CultureInfo.InvariantCulture)} and {maximum.ToString(CultureInfo.InvariantCulture)}.");
        }

        return value;
    }

    internal static IReadOnlyList<T> RequireCanonicalSet<T>(IEnumerable<T>? values, string parameterName, Func<T, string> key, int minimum = 0, int maximum = GovernedModelContractLimits.MaxSetValues)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        var declaredCount = values is IReadOnlyCollection<T> collection ? collection.Count : (int?)null;
        if (declaredCount > maximum)
        {
            throw new ArgumentException("The set count is outside the supported schema-1 bounds.", parameterName);
        }

        var snapshot = SnapshotBounded(values, parameterName, maximum);
        if (snapshot.Length < minimum || declaredCount is not null && snapshot.Length != declaredCount)
        {
            throw new ArgumentException("The set count is outside the supported schema-1 bounds.", parameterName);
        }

        var ordered = snapshot.OrderBy(key, StringComparer.Ordinal).ToArray();
        if (!snapshot.SequenceEqual(ordered) || ordered.Select(key).Distinct(StringComparer.Ordinal).Count() != ordered.Length)
        {
            throw new ArgumentException("Set values must be unique and supplied in canonical ordinal order.", parameterName);
        }

        return Array.AsReadOnly(ordered);
    }

    internal static IReadOnlyList<T> RequireOrderedUnique<T>(IEnumerable<T>? values, string parameterName, Func<T, string> key, int maximum, int minimum = 0)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        var declaredCount = values is IReadOnlyCollection<T> collection ? collection.Count : (int?)null;
        if (declaredCount > maximum)
        {
            throw new ArgumentException("Ordered values exceed the supported schema-1 bound.", parameterName);
        }

        var snapshot = SnapshotBounded(values, parameterName, maximum);
        if (snapshot.Length < minimum || declaredCount is not null && snapshot.Length != declaredCount || snapshot.Select(key).Distinct(StringComparer.Ordinal).Count() != snapshot.Length)
        {
            throw new ArgumentException("Ordered values must be bounded, non-null, and duplicate-free.", parameterName);
        }

        return Array.AsReadOnly(snapshot);
    }

    /// <summary>Creates a bounded read-only copy for already-validated internal constructor inputs, including JSON collections.</summary>
    internal static IReadOnlyList<T> RetainSnapshot<T>(IReadOnlyList<T>? values, int maximum, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if (values.Count > maximum)
        {
            throw new ArgumentException("The retained collection exceeds the supported schema-1 bound.", parameterName);
        }
        var copy = new T[values.Count];
        for (var index = 0; index < values.Count; index++)
        {
            copy[index] = values[index] ?? throw new ArgumentException("Collections cannot contain null values.", parameterName);
        }
        return Array.AsReadOnly(copy);
    }

    private static T[] SnapshotBounded<T>(IEnumerable<T> values, string parameterName, int maximum)
    {
        var snapshot = new List<T>(Math.Min(maximum, values is IReadOnlyCollection<T> collection ? collection.Count : 4));
        using var enumerator = values.GetEnumerator();
        while (enumerator.MoveNext())
        {
            if (snapshot.Count == maximum)
            {
                throw new ArgumentException("The collection exceeds the supported schema-1 bound.", parameterName);
            }

            if (enumerator.Current is null)
            {
                throw new ArgumentException("Collections cannot contain null values.", parameterName);
            }

            snapshot.Add(enumerator.Current);
        }

        return snapshot.ToArray();
    }
}
