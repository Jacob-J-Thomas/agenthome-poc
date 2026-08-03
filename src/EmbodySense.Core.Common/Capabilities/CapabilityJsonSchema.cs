namespace EmbodySense.Core.Common.Capabilities;

/// <summary>
/// Represents a bounded, canonical JSON Schema Draft 2020-12 document.
/// </summary>
public sealed class CapabilityJsonSchema : IEquatable<CapabilityJsonSchema>
{
    private CapabilityJsonSchema(string canonicalJson)
    {
        CanonicalJson = canonicalJson;
    }

    /// <summary>Gets the compact canonical JSON representation.</summary>
    public string CanonicalJson { get; }

    /// <summary>Gets the required JSON Schema Draft 2020-12 dialect identifier.</summary>
    public const string Draft202012Dialect = "https://json-schema.org/draft/2020-12/schema";

    /// <summary>
    /// Parses, bounds, and canonicalizes a JSON schema object.
    /// </summary>
    /// <param name="json">The candidate JSON schema.</param>
    /// <param name="schema">The canonical schema when successful.</param>
    /// <param name="error">The structured rejection when unsuccessful.</param>
    /// <returns><see langword="true"/> when parsing succeeds; otherwise, <see langword="false"/>.</returns>
    public static bool TryCreate(string? json, out CapabilityJsonSchema? schema, out CapabilityContractError? error)
    {
        if (!CapabilityJsonCanonicalizer.TryCanonicalizeSchema(json, out var canonicalJson, out error))
        {
            schema = null;
            return false;
        }

        schema = new CapabilityJsonSchema(canonicalJson!);
        return true;
    }

    /// <inheritdoc />
    public bool Equals(CapabilityJsonSchema? other)
    {
        return other is not null && string.Equals(CanonicalJson, other.CanonicalJson, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is CapabilityJsonSchema other && Equals(other);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return StringComparer.Ordinal.GetHashCode(CanonicalJson);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return CanonicalJson;
    }
}
