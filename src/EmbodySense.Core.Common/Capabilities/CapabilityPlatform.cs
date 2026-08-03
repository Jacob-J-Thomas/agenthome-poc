namespace EmbodySense.Core.Common.Capabilities;

/// <summary>
/// Represents one OCI-inspired operating-system and architecture compatibility tuple.
/// </summary>
public sealed class CapabilityPlatform : IEquatable<CapabilityPlatform>, IComparable<CapabilityPlatform>
{
    private CapabilityPlatform(string operatingSystem, string architecture)
    {
        OperatingSystem = operatingSystem;
        Architecture = architecture;
    }

    /// <summary>Gets the canonical operating-system token.</summary>
    public string OperatingSystem { get; }

    /// <summary>Gets the canonical architecture token.</summary>
    public string Architecture { get; }

    /// <summary>Gets the platform-independent compatibility tuple.</summary>
    public static CapabilityPlatform Any { get; } = new("any", "any");

    /// <summary>
    /// Parses a canonical <c>operating-system/architecture</c> tuple or <c>any/any</c>.
    /// </summary>
    /// <param name="value">The candidate tuple.</param>
    /// <param name="platform">The parsed platform when successful.</param>
    /// <param name="error">The structured rejection when unsuccessful.</param>
    /// <returns><see langword="true"/> when parsing succeeds; otherwise, <see langword="false"/>.</returns>
    public static bool TryParse(string? value, out CapabilityPlatform? platform, out CapabilityContractError? error)
    {
        var components = value?.Split('/');
        if (components?.Length != 2 || !CapabilityIdentifierRules.IsToken(components[0], 32) || !CapabilityIdentifierRules.IsToken(components[1], 32) || components.Contains("any", StringComparer.Ordinal) && value != "any/any")
        {
            platform = null;
            error = new CapabilityContractError("invalid_capability_platform", "$", "Platforms must use canonical lowercase operating-system/architecture tokens; 'any' may only appear as 'any/any'.");
            return false;
        }

        platform = value == "any/any" ? Any : new CapabilityPlatform(components[0], components[1]);
        error = null;
        return true;
    }

    /// <inheritdoc />
    public int CompareTo(CapabilityPlatform? other)
    {
        return other is null ? 1 : string.Compare(ToString(), other.ToString(), StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public bool Equals(CapabilityPlatform? other)
    {
        return other is not null && string.Equals(OperatingSystem, other.OperatingSystem, StringComparison.Ordinal) && string.Equals(Architecture, other.Architecture, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is CapabilityPlatform other && Equals(other);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return HashCode.Combine(StringComparer.Ordinal.GetHashCode(OperatingSystem), StringComparer.Ordinal.GetHashCode(Architecture));
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return $"{OperatingSystem}/{Architecture}";
    }
}
