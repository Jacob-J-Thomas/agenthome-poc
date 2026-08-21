using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.Models;

namespace EmbodySense.Core.Application.Loops.Execution.Custom;

/// <summary>Exposes the canonical durable run-lifecycle controls to application orchestrators.</summary>
public interface ICustomLoopLifecycleControlPort
{
    /// <summary>Requests an optimistic, idempotent pause.</summary>
    Task<CustomLoopControlResult> PauseAsync(CustomLoopPauseRequest request, CancellationToken cancellationToken = default);

    /// <summary>Requests an optimistic, idempotent cancellation.</summary>
    Task<CustomLoopControlResult> CancelAsync(CustomLoopCancelRequest request, CancellationToken cancellationToken = default);

    /// <summary>Requests an optimistic, idempotent explicit resume.</summary>
    Task<CustomLoopControlResult> ResumeAsync(CustomLoopResumeRequest request, CancellationToken cancellationToken = default);
}
