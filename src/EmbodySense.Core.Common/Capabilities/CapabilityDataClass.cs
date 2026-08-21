using EmbodySense.Core.Common.Capabilities.Models;
using System.Text.Json.Serialization;

namespace EmbodySense.Core.Common.Capabilities;

/// <summary>
/// Names one stable data classification required by a capability.
/// </summary>
[JsonConverter(typeof(CapabilityScalarJsonConverterFactory))]
public sealed class CapabilityDataClass : IEquatable<CapabilityDataClass>, IComparable<CapabilityDataClass>
{
    private CapabilityDataClass(string value)
    {
        Value = value;
    }

    /// <summary>Gets the canonical data-class token.</summary>
    public string Value { get; }

    /// <summary>
    /// Parses a canonical lowercase data-class token.
    /// </summary>
    /// <param name="value">The candidate token.</param>
    /// <param name="dataClass">The parsed data class when successful.</param>
    /// <param name="error">The structured rejection when unsuccessful.</param>
    /// <returns><see langword="true"/> when parsing succeeds; otherwise, <see langword="false"/>.</returns>
    public static bool TryParse(string? value, out CapabilityDataClass? dataClass, out CapabilityContractError? error)
    {
        if (!CapabilityIdentifierRules.IsToken(value, 64))
        {
            dataClass = null;
            error = new CapabilityContractError("invalid_data_class", "$", "Data classes must be bounded canonical lowercase ASCII tokens.");
            return false;
        }

        dataClass = new CapabilityDataClass(value!);
        error = null;
        return true;
    }

    /// <inheritdoc />
    public int CompareTo(CapabilityDataClass? other)
    {
        return other is null ? 1 : string.Compare(Value, other.Value, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public bool Equals(CapabilityDataClass? other)
    {
        return other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is CapabilityDataClass other && Equals(other);
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
