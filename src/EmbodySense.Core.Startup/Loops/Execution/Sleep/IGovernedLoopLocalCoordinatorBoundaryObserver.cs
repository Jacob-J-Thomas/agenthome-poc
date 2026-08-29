using EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

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

    /// <summary>Observes one admitted local-work family attempt after ownership and heartbeat fencing.</summary>
    /// <param name="family">The canonical work family about to invoke its one-shot runner.</param>
    /// <remarks>This callback is diagnostic only and cannot add, remove, reorder, or authorize work.</remarks>
    void OnWorkFamilyAttempted(GovernedLoopLocalWorkFamily family)
    {
    }

    /// <summary>Observes that a local session copied durable ownership-loss evidence before ending its local work.</summary>
    void OnOwnershipLost()
    {
    }

    /// <summary>Observes that the coordinator suppressed a write because the retained session no longer owns its evidence.</summary>
    void OnForeignSessionMutationSuppressed()
    {
    }
}
