namespace EmbodySense.Core.Common.Capabilities;

/// <summary>
/// Names a secret reference required by a capability without carrying a secret value.
/// </summary>
public sealed class CapabilitySecretRequirement : IEquatable<CapabilitySecretRequirement>, IComparable<CapabilitySecretRequirement>
{
    private CapabilitySecretRequirement(string name)
    {
        Name = name;
    }

    /// <summary>Gets the canonical secret-reference name.</summary>
    public string Name { get; }

    /// <summary>
    /// Parses a canonical lowercase secret-reference name.
    /// </summary>
    /// <param name="value">The candidate reference name.</param>
    /// <param name="requirement">The parsed requirement when successful.</param>
    /// <param name="error">The structured rejection when unsuccessful.</param>
    /// <returns><see langword="true"/> when parsing succeeds; otherwise, <see langword="false"/>.</returns>
    public static bool TryParse(string? value, out CapabilitySecretRequirement? requirement, out CapabilityContractError? error)
    {
        if (!CapabilityIdentifierRules.IsToken(value, 96))
        {
            requirement = null;
            error = new CapabilityContractError("invalid_secret_requirement", "$", "Secret requirements must be bounded canonical reference names and never secret values.");
            return false;
        }

        requirement = new CapabilitySecretRequirement(value!);
        error = null;
        return true;
    }

    /// <inheritdoc />
    public int CompareTo(CapabilitySecretRequirement? other)
    {
        return other is null ? 1 : string.Compare(Name, other.Name, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public bool Equals(CapabilitySecretRequirement? other)
    {
        return other is not null && string.Equals(Name, other.Name, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is CapabilitySecretRequirement other && Equals(other);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return StringComparer.Ordinal.GetHashCode(Name);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return Name;
    }
}
