using EmbodySense.Core.Application.Loops.Compatibility.Models;

namespace EmbodySense.Core.Application.Loops.Compatibility;

/// <summary>Returns validated unbound payloads together with every known gap that prevents canonical use.</summary>
public sealed class GovernedLoopCompatibilityPartialResult : GovernedLoopCompatibilityProjectionResult
{
    internal GovernedLoopCompatibilityPartialResult(GovernedLoopCompatibilitySource source, GovernedLoopCompatibilityPayload payload, IEnumerable<GovernedLoopCompatibilityGap> gaps)
        : base(source, GovernedLoopCompatibilityProjectionStatus.Partial, gaps)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (Gaps.Count == 0)
        {
            throw new ArgumentException("A partial compatibility projection requires at least one explicit gap.", nameof(gaps));
        }

        Payload = payload;
    }

    /// <summary>Gets source-derived unbound payloads that cannot become canonical runtime truth.</summary>
    public GovernedLoopCompatibilityPayload Payload { get; }
}
