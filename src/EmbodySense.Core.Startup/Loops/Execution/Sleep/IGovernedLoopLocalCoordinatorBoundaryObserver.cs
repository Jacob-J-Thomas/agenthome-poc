namespace EmbodySense.Core.Startup.Loops.Execution.Sleep;

/// <summary>Observes coordinator safe-boundary timing without granting ownership or changing durable semantics.</summary>
/// <remarks>
/// Callbacks must return promptly. They are non-authoritative diagnostics: exceptions are ignored and cannot delay,
/// suppress, approve, or otherwise alter a heartbeat or evidence mutation.
/// </remarks>
public interface IGovernedLoopLocalCoordinatorBoundaryObserver
{
    /// <summary>Observes that a heartbeat is due and is about to wait for exclusive evidence access.</summary>
    void OnHeartbeatDue();
}
