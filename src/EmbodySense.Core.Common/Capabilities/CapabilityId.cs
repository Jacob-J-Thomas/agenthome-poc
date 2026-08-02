using System.Text.Json.Serialization;

namespace EmbodySense.Core.Common.Capabilities;

/// <summary>
/// Identifies a capability with a stable provider namespace and provider-owned path.
/// </summary>
/// <remarks>The canonical schema-1 form is <c>lowercase.provider/path</c>; no normalization is performed.</remarks>
[JsonConverter(typeof(CapabilityScalarJsonConverterFactory))]
public sealed class CapabilityId : IEquatable<CapabilityId>, IComparable<CapabilityId>
{
    private CapabilityId(string value)
    {
        Value = value;
    }

    /// <summary>Gets the canonical identifier.</summary>
    public string Value { get; }

    /// <summary>
    /// Parses a canonical capability identifier without accepting aliases or normalization.
    /// </summary>
    /// <param name="value">The candidate identifier.</param>
    /// <param name="id">The parsed identifier when successful.</param>
    /// <param name="error">The structured rejection when unsuccessful.</param>
    /// <returns><see langword="true"/> when parsing succeeds; otherwise, <see langword="false"/>.</returns>
    public static bool TryParse(string? value, out CapabilityId? id, out CapabilityContractError? error)
    {
        id = null;
        if (string.IsNullOrEmpty(value) || value.Length > CapabilityContractLimits.MaxCapabilityIdCharacters)
        {
            error = new CapabilityContractError("invalid_capability_id", "$", $"Capability ids must contain between 1 and {CapabilityContractLimits.MaxCapabilityIdCharacters} characters.");
            return false;
        }

        var separator = value.IndexOf('/');
        if (separator <= 0 || separator == value.Length - 1)
        {
            error = new CapabilityContractError("invalid_capability_id", "$", "Capability ids must use the canonical lowercase provider/path form.");
            return false;
        }

        var provider = value[..separator];
        var path = value[(separator + 1)..];
        if (!CapabilityIdentifierRules.IsProviderId(provider) || !CapabilityIdentifierRules.IsPath(path, CapabilityContractLimits.MaxCapabilityIdCharacters - provider.Length - 1))
        {
            error = new CapabilityContractError("invalid_capability_id", "$", "Capability ids must use a lowercase ASCII provider namespace and bounded path segments.");
            return false;
        }

        id = new CapabilityId(value);
        error = null;
        return true;
    }

    /// <inheritdoc />
    public int CompareTo(CapabilityId? other)
    {
        return other is null ? 1 : string.Compare(Value, other.Value, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public bool Equals(CapabilityId? other)
    {
        return other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is CapabilityId other && Equals(other);
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
