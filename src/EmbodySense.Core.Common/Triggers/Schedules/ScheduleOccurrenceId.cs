namespace EmbodySense.Core.Common.Triggers.Schedules;

/// <summary>Identifies one deterministic schedule occurrence.</summary>
public sealed class ScheduleOccurrenceId : IEquatable<ScheduleOccurrenceId>, IComparable<ScheduleOccurrenceId>
{
    /// <summary>Gets the fixed schema-1 occurrence identity prefix.</summary>
    public const string Prefix = "schedule-occurrence-";

    private ScheduleOccurrenceId(string value) => Value = value;

    /// <summary>Gets the canonical occurrence identifier.</summary>
    public string Value { get; }

    /// <summary>Parses only the deterministic prefix followed by 64 lowercase hexadecimal characters.</summary>
    public static bool TryParse(string? value, out ScheduleOccurrenceId? id)
    {
        id = IsCanonical(value) ? new ScheduleOccurrenceId(value!) : null;
        return id is not null;
    }

    internal static ScheduleOccurrenceId Create(string hash) => new(Prefix + hash);

    /// <inheritdoc />
    public int CompareTo(ScheduleOccurrenceId? other) => other is null ? 1 : string.Compare(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public bool Equals(ScheduleOccurrenceId? other) => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ScheduleOccurrenceId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    /// <inheritdoc />
    public override string ToString() => Value;

    private static bool IsCanonical(string? value)
        => value?.Length == Prefix.Length + ScheduleContractLimits.Sha256HexCharacters
            && value.StartsWith(Prefix, StringComparison.Ordinal)
            && value[Prefix.Length..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
