namespace EmbodySense.Core.Common.Triggers;

/// <summary>
/// Identifies the idempotency domain of a trigger delivery without making a replay claim.
/// </summary>
public sealed class TriggerDeduplicationId : IEquatable<TriggerDeduplicationId>, IComparable<TriggerDeduplicationId>
{
    private TriggerDeduplicationId(string value) => Value = value;

    /// <summary>Gets the canonical identifier.</summary>
    public string Value { get; }

    /// <summary>
    /// Parses a bounded lowercase deduplication identifier without normalization.
    /// </summary>
    /// <param name="value">The candidate identifier.</param>
    /// <param name="id">The parsed identifier when successful.</param>
    /// <returns><see langword="true"/> when the identifier is canonical; otherwise, <see langword="false"/>.</returns>
    public static bool TryParse(string? value, out TriggerDeduplicationId? id)
    {
        id = TriggerTextRules.IsToken(value, TriggerDeliveryLimits.MaxDeduplicationIdCharacters) ? new TriggerDeduplicationId(value!) : null;
        return id is not null;
    }

    /// <inheritdoc />
    public int CompareTo(TriggerDeduplicationId? other) => other is null ? 1 : string.Compare(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public bool Equals(TriggerDeduplicationId? other) => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is TriggerDeduplicationId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    /// <inheritdoc />
    public override string ToString() => Value;
}
