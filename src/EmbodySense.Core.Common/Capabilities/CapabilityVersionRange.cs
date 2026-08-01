using System.Text.Json.Serialization;

namespace EmbodySense.Core.Common.Capabilities;

/// <summary>
/// Represents one bounded compatible-version interval using canonical NuGet-style interval notation.
/// </summary>
[JsonConverter(typeof(CapabilityScalarJsonConverterFactory))]
public sealed class CapabilityVersionRange : IEquatable<CapabilityVersionRange>
{
    private CapabilityVersionRange(string value, CapabilityVersion? minimum, bool includesMinimum, CapabilityVersion? maximum, bool includesMaximum)
    {
        Value = value;
        Minimum = minimum;
        IncludesMinimum = includesMinimum;
        Maximum = maximum;
        IncludesMaximum = includesMaximum;
    }

    /// <summary>Gets the canonical range string.</summary>
    public string Value { get; }

    /// <summary>Gets the optional minimum version.</summary>
    public CapabilityVersion? Minimum { get; }

    /// <summary>Gets a value indicating whether the minimum is included.</summary>
    public bool IncludesMinimum { get; }

    /// <summary>Gets the optional maximum version.</summary>
    public CapabilityVersion? Maximum { get; }

    /// <summary>Gets a value indicating whether the maximum is included.</summary>
    public bool IncludesMaximum { get; }

    /// <summary>Gets a value indicating whether every exact version is accepted.</summary>
    public bool IsAny => Minimum is null && Maximum is null;

    /// <summary>
    /// Parses <c>*</c>, exact <c>[1.2.3]</c>, or canonical bounded interval notation.
    /// </summary>
    /// <param name="value">The candidate compatible-version range.</param>
    /// <param name="range">The parsed range when successful.</param>
    /// <param name="error">The structured rejection when unsuccessful.</param>
    /// <returns><see langword="true"/> when parsing succeeds; otherwise, <see langword="false"/>.</returns>
    public static bool TryParse(string? value, out CapabilityVersionRange? range, out CapabilityContractError? error)
    {
        range = null;
        if (string.IsNullOrEmpty(value) || value.Length > CapabilityContractLimits.MaxVersionRangeCharacters || value.Any(char.IsWhiteSpace))
        {
            error = Invalid("Compatible version ranges must be non-empty, bounded, and free of whitespace.");
            return false;
        }

        if (value == "*")
        {
            range = new CapabilityVersionRange(value, null, false, null, false);
            error = null;
            return true;
        }

        if (value[0] == '[' && value[^1] == ']' && !value.Contains(','))
        {
            if (!CapabilityVersion.TryParse(value[1..^1], out var exact, out _) || exact?.BuildMetadata is not null)
            {
                error = Invalid("Exact ranges must wrap one canonical exact version without build metadata in square brackets.");
                return false;
            }

            range = new CapabilityVersionRange(value, exact, true, exact, true);
            error = null;
            return true;
        }

        if (value.Length < 3 || value[0] is not '[' and not '(' || value[^1] is not ']' and not ')' || value.Count(character => character == ',') != 1)
        {
            error = Invalid("Compatible version ranges must use canonical interval notation.");
            return false;
        }

        var bounds = value[1..^1].Split(',');
        var minimumText = bounds[0];
        var maximumText = bounds[1];
        if (minimumText.Length == 0 && value[0] != '(' || maximumText.Length == 0 && value[^1] != ')')
        {
            error = Invalid("Unbounded interval endpoints must be exclusive.");
            return false;
        }

        CapabilityVersion? minimum = null;
        CapabilityVersion? maximum = null;
        if (minimumText.Length > 0 && !CapabilityVersion.TryParse(minimumText, out minimum, out _) || maximumText.Length > 0 && !CapabilityVersion.TryParse(maximumText, out maximum, out _))
        {
            error = Invalid("Every interval endpoint must be a canonical exact version.");
            return false;
        }

        if (minimum?.BuildMetadata is not null || maximum?.BuildMetadata is not null)
        {
            error = Invalid("Compatible range endpoints cannot contain build metadata because range membership ignores it.");
            return false;
        }

        if (minimum is null && maximum is null)
        {
            error = Invalid("Use '*' for an unbounded compatible-version range.");
            return false;
        }

        if (minimum is not null && maximum is not null)
        {
            var comparison = minimum.ComparePrecedenceTo(maximum);
            if (comparison > 0 || comparison == 0)
            {
                error = Invalid("Interval bounds must describe a non-empty range; use '[version]' for an exact range.");
                return false;
            }
        }

        range = new CapabilityVersionRange(value, minimum, value[0] == '[', maximum, value[^1] == ']');
        error = null;
        return true;
    }

    /// <summary>
    /// Determines whether an exact version has precedence within this range.
    /// </summary>
    /// <param name="version">The exact version to test.</param>
    /// <returns><see langword="true"/> when the version is compatible; otherwise, <see langword="false"/>.</returns>
    public bool Contains(CapabilityVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (Minimum is not null)
        {
            var lower = version.ComparePrecedenceTo(Minimum);
            if (lower < 0 || lower == 0 && !IncludesMinimum)
            {
                return false;
            }
        }

        if (Maximum is not null)
        {
            var upper = version.ComparePrecedenceTo(Maximum);
            if (upper > 0 || upper == 0 && !IncludesMaximum)
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public bool Equals(CapabilityVersionRange? other)
    {
        return other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is CapabilityVersionRange other && Equals(other);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return StringComparer.Ordinal.GetHashCode(Value);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return Value;
    }

    private static CapabilityContractError Invalid(string message)
    {
        return new CapabilityContractError("invalid_capability_version_range", "$", message);
    }
}
