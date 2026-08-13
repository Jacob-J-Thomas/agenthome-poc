using EmbodySense.Core.Common.Loops.Custom;

namespace EmbodySense.Core.Common.Triggers.Schedules;

/// <summary>Identifies one schedule without granting authority to evaluate or deliver it.</summary>
public sealed class ScheduleId : IEquatable<ScheduleId>, IComparable<ScheduleId>
{
    private ScheduleId(string value) => Value = value;

    /// <summary>Gets the canonical identifier.</summary>
    public string Value { get; }

    /// <summary>Parses a bounded filename-safe lowercase schedule identifier.</summary>
    public static bool TryParse(string? value, out ScheduleId? id)
    {
        id = CustomLoopArtifactIdentifier.IsValid(value, ScheduleContractLimits.MaxScheduleIdCharacters)
            ? new ScheduleId(value!)
            : null;
        return id is not null;
    }

    /// <inheritdoc />
    public int CompareTo(ScheduleId? other) => other is null ? 1 : string.Compare(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public bool Equals(ScheduleId? other) => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ScheduleId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    /// <inheritdoc />
    public override string ToString() => Value;
}
