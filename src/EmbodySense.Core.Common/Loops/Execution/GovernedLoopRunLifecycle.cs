namespace EmbodySense.Core.Common.Loops.Execution;

/// <summary>Binds lifecycle evidence to one exact governed-loop execution.</summary>
/// <remarks>Construction preserves the distinction between reusable payloads and canonical bound evidence.</remarks>
public sealed record GovernedLoopRunLifecycle
{
    private GovernedLoopRunLifecycle(GovernedLoopExecutionBinding binding, GovernedLoopRunLifecyclePayload payload)
    {
        Binding = binding;
        Payload = payload;
    }

    /// <summary>Gets the exact execution binding.</summary>
    public GovernedLoopExecutionBinding Binding { get; }

    /// <summary>Gets the reusable unbound lifecycle payload.</summary>
    public GovernedLoopRunLifecyclePayload Payload { get; }

    /// <summary>Creates bound lifecycle evidence.</summary>
    /// <param name="binding">The exact execution binding.</param>
    /// <param name="payload">The validated lifecycle payload.</param>
    /// <returns>The bound lifecycle evidence.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="binding"/> or <paramref name="payload"/> is <see langword="null"/>.</exception>
    public static GovernedLoopRunLifecycle Create(GovernedLoopExecutionBinding binding, GovernedLoopRunLifecyclePayload payload)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(payload);
        return new GovernedLoopRunLifecycle(binding, payload);
    }
}
