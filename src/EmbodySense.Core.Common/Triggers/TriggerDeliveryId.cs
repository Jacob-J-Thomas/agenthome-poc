namespace EmbodySense.Core.Common.Triggers;

/// <summary>
/// Identifies one trigger delivery without granting authority to admit it.
/// </summary>
public sealed class TriggerDeliveryId : IEquatable<TriggerDeliveryId>, IComparable<TriggerDeliveryId>
{
    private TriggerDeliveryId(string value) => Value = value;

    /// <summary>Gets the canonical identifier.</summary>
    public string Value { get; }

    /// <summary>
    /// Parses a bounded lowercase delivery identifier without normalization.
    /// </summary>
    /// <param name="value">The candidate identifier.</param>
    /// <param name="id">The parsed identifier when successful.</param>
    /// <returns><see langword="true"/> when the identifier is canonical; otherwise, <see langword="false"/>.</returns>
    public static bool TryParse(string? value, out TriggerDeliveryId? id)
    {
        id = TriggerTextRules.IsToken(value, TriggerDeliveryLimits.MaxDeliveryIdCharacters) ? new TriggerDeliveryId(value!) : null;
        return id is not null;
    }

    /// <inheritdoc />
    public int CompareTo(TriggerDeliveryId? other) => other is null ? 1 : string.Compare(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public bool Equals(TriggerDeliveryId? other) => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is TriggerDeliveryId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    /// <inheritdoc />
    public override string ToString() => Value;
}
