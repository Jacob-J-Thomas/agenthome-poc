using EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Startup.Loops.Execution.Sleep;

/// <summary>Exposes the bounded Startup recovery boundary used by the canonical background host.</summary>
/// <remarks>
/// Implementations own only process-local scan posture. Durable run, review, continuation, action, claim, and release
/// evidence remains in the Application and Persistence ports supplied at composition time.
/// </remarks>
public interface IHumanReviewRecoveryRunner : IGovernedLoopLocalWorkRunner, IGovernedLoopLocalWorkReadinessProbe
{
    /// <summary>Gets whether a clean bounded dependency probe has established executable recovery posture.</summary>
    bool IsExecutable { get; }

    /// <summary>Runs one bounded startup or steady-state recovery pass over all Human Review lanes.</summary>
    /// <param name="cancellationToken">Cancels before a definitive pass outcome is available.</param>
    /// <returns>The detached publication outcome and opaque continuation/action scan posture.</returns>
    Task<HumanReviewRecoveryPassResult> RecoverAsync(CancellationToken cancellationToken = default);
}
