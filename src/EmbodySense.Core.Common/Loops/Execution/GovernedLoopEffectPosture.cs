namespace EmbodySense.Core.Common.Loops.Execution;

/// <summary>Binds one effect payload to one exact governed-loop execution.</summary>
/// <remarks>Construction preserves the distinction between reusable payloads and canonical bound evidence.</remarks>
public sealed record GovernedLoopEffectPosture
{
    private GovernedLoopEffectPosture(GovernedLoopExecutionBinding binding, GovernedLoopEffectPayload payload)
    {
        Binding = binding;
        Payload = payload;
    }

    /// <summary>Gets the exact execution binding.</summary>
    public GovernedLoopExecutionBinding Binding { get; }

    /// <summary>Gets the reusable unbound effect payload.</summary>
    public GovernedLoopEffectPayload Payload { get; }

    /// <summary>Creates bound effect evidence.</summary>
    /// <param name="binding">The exact execution binding.</param>
    /// <param name="payload">The validated effect payload.</param>
    /// <returns>The bound effect posture.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="binding"/> or <paramref name="payload"/> is <see langword="null"/>.</exception>
    public static GovernedLoopEffectPosture Create(GovernedLoopExecutionBinding binding, GovernedLoopEffectPayload payload)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(payload);
        return new GovernedLoopEffectPosture(binding, payload);
    }
}
