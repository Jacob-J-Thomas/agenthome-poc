using EmbodySense.Core.Application.Loops.Execution.Effects.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Effects;

/// <summary>Executes one canonical crash-safe server-registered actuator attempt.</summary>
public interface IGovernedLoopEffectAttemptService
{
    /// <summary>Prepares immutable intent, revalidates authority, and executes or replays one exact effect attempt.</summary>
    Task<GovernedLoopEffectAttemptExecutionResult> ExecuteAsync(
        GovernedLoopEffectAttemptRequest request,
        CancellationToken cancellationToken = default);
}
