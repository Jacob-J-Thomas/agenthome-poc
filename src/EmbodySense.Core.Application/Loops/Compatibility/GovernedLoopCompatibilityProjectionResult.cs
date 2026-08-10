using EmbodySense.Core.Application.Loops.Compatibility.Models;

namespace EmbodySense.Core.Application.Loops.Compatibility;

/// <summary>Represents one discriminated, read-only projection of legacy evidence toward canonical governed execution.</summary>
public abstract class GovernedLoopCompatibilityProjectionResult
{
    private readonly IReadOnlyList<GovernedLoopCompatibilityGap> _gaps;

    internal GovernedLoopCompatibilityProjectionResult(GovernedLoopCompatibilitySource source, GovernedLoopCompatibilityProjectionStatus status, IEnumerable<GovernedLoopCompatibilityGap> gaps)
    {
        if (!Enum.IsDefined(source) || source == GovernedLoopCompatibilitySource.Unknown)
        {
            throw new ArgumentOutOfRangeException(nameof(source), source, "Choose a supported compatibility source.");
        }

        if (!Enum.IsDefined(status) || status == GovernedLoopCompatibilityProjectionStatus.Unknown)
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Choose a concrete compatibility status.");
        }

        ArgumentNullException.ThrowIfNull(gaps);
        var bounded = gaps.Take(GovernedLoopCompatibilityLimits.MaxGaps + 1).ToArray();
        if (bounded.Length > GovernedLoopCompatibilityLimits.MaxGaps)
        {
            throw new ArgumentOutOfRangeException(nameof(gaps), $"Compatibility results cannot contain more than {GovernedLoopCompatibilityLimits.MaxGaps} gaps.");
        }

        if (bounded.Any(gap => gap is null))
        {
            throw new ArgumentException("Compatibility gaps cannot contain null entries.", nameof(gaps));
        }

        Source = source;
        Status = status;
        _gaps = Array.AsReadOnly(bounded
            .OrderBy(gap => gap.Code)
            .DistinctBy(gap => gap.Code)
            .ToArray());
    }

    /// <summary>Gets the validated legacy source kind.</summary>
    public GovernedLoopCompatibilitySource Source { get; }

    /// <summary>Gets the projection discriminator.</summary>
    public GovernedLoopCompatibilityProjectionStatus Status { get; }

    /// <summary>Gets a defensive, code-sorted list of explicit compatibility gaps.</summary>
    public IReadOnlyList<GovernedLoopCompatibilityGap> Gaps => _gaps;
}
