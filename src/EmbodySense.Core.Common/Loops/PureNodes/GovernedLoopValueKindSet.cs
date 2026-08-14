using System.Collections.ObjectModel;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Common.Loops.PureNodes;

/// <summary>Represents one non-empty, sorted, unique, bounded set of portable value kinds.</summary>
/// <remarks>This value replaces scalar catalog-kind assumptions for descriptors whose exact port contract admits more than one portable kind.</remarks>
public sealed class GovernedLoopValueKindSet : IEquatable<GovernedLoopValueKindSet>
{
    private GovernedLoopValueKindSet(GovernedLoopValueKind[] kinds)
    {
        Kinds = new ReadOnlyCollection<GovernedLoopValueKind>(kinds);
    }

    /// <summary>Gets the canonical kinds in enum order.</summary>
    /// <value>The immutable sorted unique kind set.</value>
    public IReadOnlyList<GovernedLoopValueKind> Kinds { get; }

    /// <summary>Creates a canonical non-empty kind set.</summary>
    /// <param name="kinds">The defined portable kinds.</param>
    /// <returns>An immutable canonical kind set.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="kinds"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when the set is empty, duplicated, oversized, or contains an undefined kind.</exception>
    public static GovernedLoopValueKindSet Create(IEnumerable<GovernedLoopValueKind> kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        var maximum = Enum.GetValues<GovernedLoopValueKind>().Count(value => value != GovernedLoopValueKind.Unknown);
        GovernedLoopValueKind[] values;
        try
        {
            values = kinds.Take(maximum + 1).ToArray();
        }
        catch (Exception exception) when (exception is not (StackOverflowException or OutOfMemoryException))
        {
            throw new ArgumentException("Value-kind sets must be inspectable within the bounded contract.", nameof(kinds), exception);
        }

        if (values.Length < 1 || values.Length > maximum || values.Any(value => !Enum.IsDefined(value) || value == GovernedLoopValueKind.Unknown) || values.Distinct().Count() != values.Length)
        {
            throw new ArgumentException("Value-kind sets must be non-empty, bounded, unique, and fully defined.", nameof(kinds));
        }

        return new GovernedLoopValueKindSet(values.Order().ToArray());
    }

    /// <summary>Returns whether the exact kind belongs to this set.</summary>
    /// <param name="kind">The candidate kind.</param>
    /// <returns><see langword="true"/> when the kind is present.</returns>
    public bool Contains(GovernedLoopValueKind kind) => Kinds.Contains(kind);

    /// <inheritdoc />
    public bool Equals(GovernedLoopValueKindSet? other) => other is not null && Kinds.SequenceEqual(other.Kinds);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is GovernedLoopValueKindSet other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var kind in Kinds)
        {
            hash.Add(kind);
        }

        return hash.ToHashCode();
    }
}
