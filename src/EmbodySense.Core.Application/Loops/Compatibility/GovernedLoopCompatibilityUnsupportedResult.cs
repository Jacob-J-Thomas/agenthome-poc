using EmbodySense.Core.Application.Loops.Compatibility.Models;

namespace EmbodySense.Core.Application.Loops.Compatibility;

/// <summary>Reports that a legacy source could not be safely projected and therefore yielded no source fields.</summary>
public sealed class GovernedLoopCompatibilityUnsupportedResult : GovernedLoopCompatibilityProjectionResult
{
    internal GovernedLoopCompatibilityUnsupportedResult(
        GovernedLoopCompatibilitySource source,
        GovernedLoopCompatibilityGapCode cause = GovernedLoopCompatibilityGapCode.SourceValidationFailed)
        : base(source, GovernedLoopCompatibilityProjectionStatus.Unsupported, [CreateCause(cause)])
    {
    }

    private static GovernedLoopCompatibilityGap CreateCause(GovernedLoopCompatibilityGapCode cause)
    {
        if (cause is not (GovernedLoopCompatibilityGapCode.SourceValidationFailed or GovernedLoopCompatibilityGapCode.AdapterInputBoundsExceeded))
        {
            throw new ArgumentOutOfRangeException(nameof(cause), cause, "Choose a supported unsupported-result cause.");
        }

        return GovernedLoopCompatibilityGap.Create(cause);
    }
}
