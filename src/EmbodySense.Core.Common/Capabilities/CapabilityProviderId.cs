using System.Text.Json.Serialization;

namespace EmbodySense.Core.Common.Capabilities;

/// <summary>
/// Identifies the provider responsible for a capability implementation.
/// </summary>
[JsonConverter(typeof(CapabilityScalarJsonConverterFactory))]
public sealed class CapabilityProviderId : IEquatable<CapabilityProviderId>, IComparable<CapabilityProviderId>
{
    private CapabilityProviderId(string value)
    {
        Value = value;
    }

    /// <summary>Gets the canonical lowercase provider namespace.</summary>
    public string Value { get; }

    /// <summary>
    /// Parses a canonical reverse-domain-style provider identifier.
    /// </summary>
    /// <param name="value">The candidate provider identifier.</param>
    /// <param name="providerId">The parsed provider identifier when successful.</param>
    /// <param name="error">The structured rejection when unsuccessful.</param>
    /// <returns><see langword="true"/> when parsing succeeds; otherwise, <see langword="false"/>.</returns>
    public static bool TryParse(string? value, out CapabilityProviderId? providerId, out CapabilityContractError? error)
    {
        if (!CapabilityIdentifierRules.IsProviderId(value))
        {
            providerId = null;
            error = new CapabilityContractError("invalid_provider_id", "$", "Provider ids must be canonical lowercase ASCII domain-style namespaces with at least two labels.");
            return false;
        }

        providerId = new CapabilityProviderId(value!);
        error = null;
        return true;
    }

    /// <inheritdoc />
    public int CompareTo(CapabilityProviderId? other)
    {
        return other is null ? 1 : string.Compare(Value, other.Value, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public bool Equals(CapabilityProviderId? other)
    {
        return other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is CapabilityProviderId other && Equals(other);
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
}
