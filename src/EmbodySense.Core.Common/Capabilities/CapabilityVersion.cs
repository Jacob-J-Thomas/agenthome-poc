using System.Globalization;
using System.Text.Json.Serialization;

namespace EmbodySense.Core.Common.Capabilities;

/// <summary>
/// Represents one exact, bounded Semantic Versioning 2.0.0 version.
/// </summary>
/// <remarks>Precedence follows SemVer; deterministic total ordering uses build metadata as an ordinal tie-breaker.</remarks>
[JsonConverter(typeof(CapabilityScalarJsonConverterFactory))]
public sealed class CapabilityVersion : IEquatable<CapabilityVersion>, IComparable<CapabilityVersion>
{
    private readonly IReadOnlyList<string> _preReleaseIdentifiers;

    private CapabilityVersion(string value, int major, int minor, int patch, string[] preReleaseIdentifiers, string? buildMetadata)
    {
        Value = value;
        Major = major;
        Minor = minor;
        Patch = patch;
        _preReleaseIdentifiers = Array.AsReadOnly(preReleaseIdentifiers);
        BuildMetadata = buildMetadata;
    }

    /// <summary>Gets the canonical exact version string.</summary>
    public string Value { get; }

    /// <summary>Gets the major component.</summary>
    public int Major { get; }

    /// <summary>Gets the minor component.</summary>
    public int Minor { get; }

    /// <summary>Gets the patch component.</summary>
    public int Patch { get; }

    /// <summary>Gets the pre-release identifiers.</summary>
    public IReadOnlyList<string> PreReleaseIdentifiers => _preReleaseIdentifiers;

    /// <summary>Gets the optional build metadata without its leading plus sign.</summary>
    public string? BuildMetadata { get; }

    /// <summary>Gets a value indicating whether this is a pre-release version.</summary>
    public bool IsPreRelease => _preReleaseIdentifiers.Count > 0;

    /// <summary>
    /// Parses an exact canonical SemVer 2.0.0 value without trimming, case-folding, or accepting abbreviated versions.
    /// </summary>
    /// <param name="value">The candidate version.</param>
    /// <param name="version">The parsed version when successful.</param>
    /// <param name="error">The structured rejection when unsuccessful.</param>
    /// <returns><see langword="true"/> when parsing succeeds; otherwise, <see langword="false"/>.</returns>
    public static bool TryParse(string? value, out CapabilityVersion? version, out CapabilityContractError? error)
    {
        version = null;
        if (string.IsNullOrEmpty(value) || value.Length > CapabilityContractLimits.MaxVersionCharacters || value.Any(character => character > 0x7f))
        {
            error = Invalid("Exact versions must be non-empty bounded ASCII SemVer 2.0.0 strings.");
            return false;
        }

        var plus = value.IndexOf('+');
        if (plus >= 0 && value.IndexOf('+', plus + 1) >= 0)
        {
            error = Invalid("Exact versions may contain at most one build-metadata separator.");
            return false;
        }

        var versionWithoutBuild = plus < 0 ? value : value[..plus];
        var buildMetadata = plus < 0 ? null : value[(plus + 1)..];
        if (buildMetadata is not null && !AreIdentifiersValid(buildMetadata, allowNumericLeadingZero: true))
        {
            error = Invalid("Build metadata must contain non-empty ASCII alphanumeric or hyphen identifiers.");
            return false;
        }

        var dash = versionWithoutBuild.IndexOf('-');
        var core = dash < 0 ? versionWithoutBuild : versionWithoutBuild[..dash];
        var preRelease = dash < 0 ? null : versionWithoutBuild[(dash + 1)..];
        if (preRelease is not null && !AreIdentifiersValid(preRelease, allowNumericLeadingZero: false))
        {
            error = Invalid("Pre-release identifiers must be canonical SemVer identifiers without numeric leading zeroes.");
            return false;
        }

        var components = core.Split('.');
        if (components.Length != 3 || !TryParseCore(components[0], out var major) || !TryParseCore(components[1], out var minor) || !TryParseCore(components[2], out var patch))
        {
            error = Invalid("Exact versions must contain three bounded decimal components without leading zeroes.");
            return false;
        }

        version = new CapabilityVersion(value, major, minor, patch, preRelease?.Split('.') ?? [], buildMetadata);
        error = null;
        return true;
    }

    /// <summary>
    /// Compares SemVer precedence while ignoring build metadata.
    /// </summary>
    /// <param name="other">The other exact version.</param>
    /// <returns>A signed precedence comparison result.</returns>
    public int ComparePrecedenceTo(CapabilityVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var comparison = Major.CompareTo(other.Major);
        comparison = comparison != 0 ? comparison : Minor.CompareTo(other.Minor);
        comparison = comparison != 0 ? comparison : Patch.CompareTo(other.Patch);
        if (comparison != 0)
        {
            return comparison;
        }

        if (_preReleaseIdentifiers.Count == 0 || other._preReleaseIdentifiers.Count == 0)
        {
            return _preReleaseIdentifiers.Count == other._preReleaseIdentifiers.Count ? 0 : _preReleaseIdentifiers.Count == 0 ? 1 : -1;
        }

        for (var index = 0; index < Math.Min(_preReleaseIdentifiers.Count, other._preReleaseIdentifiers.Count); index++)
        {
            comparison = ComparePreReleaseIdentifier(_preReleaseIdentifiers[index], other._preReleaseIdentifiers[index]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return _preReleaseIdentifiers.Count.CompareTo(other._preReleaseIdentifiers.Count);
    }

    /// <inheritdoc />
    public int CompareTo(CapabilityVersion? other)
    {
        var precedence = ComparePrecedenceTo(other);
        return precedence != 0 || other is null ? precedence : string.Compare(Value, other.Value, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public bool Equals(CapabilityVersion? other)
    {
        return other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is CapabilityVersion other && Equals(other);
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
        return new CapabilityContractError("invalid_capability_version", "$", message);
    }

    private static bool TryParseCore(string value, out int component)
    {
        component = 0;
        return value.Length > 0 && (value.Length == 1 || value[0] != '0') && value.All(character => character is >= '0' and <= '9') && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out component);
    }

    private static bool AreIdentifiersValid(string value, bool allowNumericLeadingZero)
    {
        foreach (var identifier in value.Split('.'))
        {
            if (identifier.Length == 0 || identifier.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
            {
                return false;
            }

            var numeric = identifier.All(character => character is >= '0' and <= '9');
            if (!allowNumericLeadingZero && numeric && identifier.Length > 1 && identifier[0] == '0')
            {
                return false;
            }
        }

        return true;
    }

    private static int ComparePreReleaseIdentifier(string left, string right)
    {
        var leftNumeric = left.All(character => character is >= '0' and <= '9');
        var rightNumeric = right.All(character => character is >= '0' and <= '9');
        if (leftNumeric && rightNumeric)
        {
            return left.Length != right.Length ? left.Length.CompareTo(right.Length) : string.Compare(left, right, StringComparison.Ordinal);
        }

        if (leftNumeric != rightNumeric)
        {
            return leftNumeric ? -1 : 1;
        }

        return string.Compare(left, right, StringComparison.Ordinal);
    }
}
