namespace EmbodySense.Core.Common.Loops.Execution;

/// <summary>Binds one projection payload to one exact governed-loop execution.</summary>
/// <remarks>Construction preserves the distinction between reusable payloads and canonical bound evidence.</remarks>
public sealed record GovernedLoopProjectionPosture
{
    private GovernedLoopProjectionPosture(GovernedLoopExecutionBinding binding, GovernedLoopProjectionPayload payload)
    {
        Binding = binding;
        Payload = payload;
    }

    /// <summary>Gets the exact execution binding.</summary>
    public GovernedLoopExecutionBinding Binding { get; }

    /// <summary>Gets the reusable unbound projection payload.</summary>
    public GovernedLoopProjectionPayload Payload { get; }

    /// <summary>Creates bound projection evidence.</summary>
    /// <param name="binding">The exact execution binding.</param>
    /// <param name="payload">The validated projection payload.</param>
    /// <returns>The bound projection posture.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="binding"/> or <paramref name="payload"/> is <see langword="null"/>.</exception>
    public static GovernedLoopProjectionPosture Create(GovernedLoopExecutionBinding binding, GovernedLoopProjectionPayload payload)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(payload);
        return new GovernedLoopProjectionPosture(binding, payload);
    }
}
