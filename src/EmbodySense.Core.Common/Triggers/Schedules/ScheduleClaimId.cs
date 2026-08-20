using EmbodySense.Core.Common.Loops.Custom;

namespace EmbodySense.Core.Common.Triggers.Schedules;

/// <summary>Identifies one optimistic due-occurrence claim without acting as a lease or grant.</summary>
public sealed class ScheduleClaimId : IEquatable<ScheduleClaimId>, IComparable<ScheduleClaimId>
{
    private ScheduleClaimId(string value) => Value = value;

    /// <summary>Gets the canonical claim identifier.</summary>
    public string Value { get; }

    /// <summary>Parses a bounded filename-safe lowercase claim identifier.</summary>
    public static bool TryParse(string? value, out ScheduleClaimId? id)
    {
        id = CustomLoopArtifactIdentifier.IsValid(value, ScheduleContractLimits.MaxClaimIdCharacters)
            ? new ScheduleClaimId(value!)
            : null;
        return id is not null;
    }

    /// <inheritdoc />
    public int CompareTo(ScheduleClaimId? other) => other is null ? 1 : string.Compare(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public bool Equals(ScheduleClaimId? other) => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ScheduleClaimId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    /// <inheritdoc />
    public override string ToString() => Value;
}
