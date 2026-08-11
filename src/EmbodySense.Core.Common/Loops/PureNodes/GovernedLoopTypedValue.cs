using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Common.Loops.PureNodes;

/// <summary>Contains one immutable bounded schema-1 typed value and its canonical evidence identity.</summary>
public sealed class GovernedLoopTypedValue : IEquatable<GovernedLoopTypedValue>
{
    internal GovernedLoopTypedValue(GovernedLoopValueKind kind, string canonicalValueJson, string canonicalJson, string contentHash)
    {
        Kind = kind;
        CanonicalValueJson = canonicalValueJson;
        CanonicalJson = canonicalJson;
        ContentHash = contentHash;
    }

    /// <summary>The only supported typed-value schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets the schema version.</summary>
    /// <value>Always <see cref="CurrentSchemaVersion"/>.</value>
    public int SchemaVersion => CurrentSchemaVersion;
    /// <summary>Gets the declared portable kind.</summary>
    /// <value>The exact non-Binary value kind.</value>
    public GovernedLoopValueKind Kind { get; }
    /// <summary>Gets the canonical JSON for the value payload alone.</summary>
    /// <value>The recursively canonicalized JSON value.</value>
    public string CanonicalValueJson { get; }
    /// <summary>Gets the exact canonical schema-1 envelope JSON.</summary>
    /// <value>The durable byte-stable value envelope.</value>
    public string CanonicalJson { get; }
    /// <summary>Gets the lowercase SHA-256 digest of <see cref="CanonicalJson"/>.</summary>
    /// <value>The exact value evidence identity.</value>
    public string ContentHash { get; }
    /// <summary>Gets whether the canonical payload is JSON null.</summary>
    /// <value><see langword="true"/> only for an explicit null payload.</value>
    public bool IsNull => string.Equals(CanonicalValueJson, "null", StringComparison.Ordinal);

    /// <summary>Creates one typed value by recursively canonicalizing an exact JSON payload.</summary>
    /// <param name="schemaVersion">The schema version, which must be 1.</param>
    /// <param name="kind">The exact non-Binary declared kind.</param>
    /// <param name="valueJson">The bounded JSON payload.</param>
    /// <param name="value">The immutable canonical value on success.</param>
    /// <param name="validation">The deterministic validation result.</param>
    /// <returns><see langword="true"/> only when the value was created.</returns>
    public static bool TryCreate(int schemaVersion, GovernedLoopValueKind kind, string? valueJson, out GovernedLoopTypedValue? value, out GovernedLoopTypedValueValidationResult validation)
        => GovernedLoopTypedValueJson.TryCreate(schemaVersion, kind, valueJson, out value, out validation);

    /// <summary>Reads one exact canonical envelope without compatibility aliases or fallback normalization.</summary>
    /// <param name="json">The candidate canonical schema-1 envelope.</param>
    /// <param name="value">The immutable typed value on success.</param>
    /// <param name="validation">The deterministic validation result.</param>
    /// <returns><see langword="true"/> only when the input is already byte-for-byte canonical.</returns>
    public static bool TryDeserialize(string? json, out GovernedLoopTypedValue? value, out GovernedLoopTypedValueValidationResult validation)
        => GovernedLoopTypedValueJson.TryDeserialize(json, out value, out validation);

    /// <inheritdoc />
    public bool Equals(GovernedLoopTypedValue? other) => other is not null && string.Equals(CanonicalJson, other.CanonicalJson, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is GovernedLoopTypedValue other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(CanonicalJson);
}
