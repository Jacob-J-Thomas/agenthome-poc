using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;

namespace EmbodySense.Core.Application.HumanInput.Lifecycle;

/// <summary>Executes authenticated, idempotent Human Input request lifecycle operations.</summary>
public interface IHumanInputRequestLifecycleService
{
    /// <summary>Validates, authorizes, and durably applies one exact lifecycle command.</summary>
    /// <param name="command">The caller-owned bounded command, which carries no authoritative actor, time, or workspace claim.</param>
    /// <param name="cancellationToken">A token that cancels work before durable intent begins.</param>
    /// <returns>A privacy-safe exact operation result.</returns>
    Task<HumanInputRequestLifecycleMutationResult> MutateAsync(HumanInputRequestLifecycleCommand? command, CancellationToken cancellationToken = default);
}
