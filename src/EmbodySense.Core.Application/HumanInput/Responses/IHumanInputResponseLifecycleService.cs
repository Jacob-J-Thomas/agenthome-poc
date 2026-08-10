using EmbodySense.Core.Application.HumanInput.Responses.Models;

namespace EmbodySense.Core.Application.HumanInput.Responses;

/// <summary>Authenticates, validates, and durably records Human Input response lifecycle operations.</summary>
public interface IHumanInputResponseLifecycleService
{
    /// <summary>Applies one exact response operation without accepting caller-owned actor, time, or authority claims.</summary>
    /// <param name="command">The bounded caller-owned operation intent.</param>
    /// <param name="cancellationToken">A token that cancels work before durable intent begins.</param>
    /// <returns>The privacy-safe durable, replayed, rejected, or fail-closed outcome.</returns>
    Task<HumanInputResponseLifecycleMutationResult> MutateAsync(HumanInputResponseLifecycleCommand? command, CancellationToken cancellationToken = default);
}
