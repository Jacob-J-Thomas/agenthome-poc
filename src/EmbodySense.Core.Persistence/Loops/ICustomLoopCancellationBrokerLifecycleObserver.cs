namespace EmbodySense.Core.Persistence.Loops;

/// <summary>
/// Observes the bounded lifecycle transitions of a local custom-loop cancellation broker.
/// </summary>
/// <remarks>
/// Observers run in the owning process. They must return promptly and must not treat the named-pipe name as authentication;
/// only the descriptor's generation-bound secret authenticates a cancellation request. Fault-observer failures cannot prevent
/// the owner from withdrawing its descriptor and retiring its active attempts.
/// </remarks>
public interface ICustomLoopCancellationBrokerLifecycleObserver
{
    /// <summary>
    /// Observes that the exact broker generation is accepting connections but has not published its owner descriptor yet.
    /// </summary>
    /// <param name="pipeName">The generation-specific named-pipe endpoint.</param>
    void OnBrokerReadyBeforeOwnerDescriptorPublication(string pipeName);

    /// <summary>
    /// Observes a terminal broker fault after the exact owner descriptor is withdrawn and before workspace-host retirement runs.
    /// </summary>
    void OnBrokerFaulted();
}
