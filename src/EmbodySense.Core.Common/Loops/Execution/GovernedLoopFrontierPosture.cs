namespace EmbodySense.Core.Common.Loops.Execution.Models;

/// <summary>Binds committed frontier evidence to one exact governed-loop execution.</summary>
/// <remarks>Construction preserves the distinction between reusable payloads and canonical bound evidence.</remarks>
public sealed record GovernedLoopFrontierPosture
{
    private GovernedLoopFrontierPosture(GovernedLoopExecutionBinding binding, GovernedLoopFrontierPayload payload)
    {
        Binding = binding;
        Payload = payload;
    }

    /// <summary>Gets the exact execution binding.</summary>
    public GovernedLoopExecutionBinding Binding { get; }

    /// <summary>Gets the reusable unbound frontier payload.</summary>
    public GovernedLoopFrontierPayload Payload { get; }

    /// <summary>Creates bound frontier evidence.</summary>
    /// <param name="binding">The exact execution binding.</param>
    /// <param name="payload">The validated frontier payload.</param>
    /// <returns>The bound frontier evidence.</returns>
    public static GovernedLoopFrontierPosture Create(GovernedLoopExecutionBinding binding, GovernedLoopFrontierPayload payload)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(payload);
        return new GovernedLoopFrontierPosture(binding, payload);
    }
}
