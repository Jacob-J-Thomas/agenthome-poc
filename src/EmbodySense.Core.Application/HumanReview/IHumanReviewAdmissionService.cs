using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Loops;

namespace EmbodySense.Core.Application.HumanReview;

/// <summary>Atomically commits one predecessor-to-blocked frontier transition with its exact immutable Human Review request.</summary>
public interface IHumanReviewAdmissionService
{
    /// <summary>Persists the request, pending lifecycle, evidence, and proposed ReviewBlocked frontier through one run-store update.</summary>
    /// <param name="command">The exact admission command.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The canonical run-store result.</returns>
    /// <exception cref="ArgumentNullException">Thrown when the command or either required contract is null.</exception>
    /// <exception cref="FormatException">Thrown when the canonical store refuses an oversized or corrupt persisted artifact before publication.</exception>
    /// <exception cref="OperationCanceledException">Thrown when cancellation interrupts the canonical store operation.</exception>
    Task<CustomLoopRunStoreResult> AdmitAsync(HumanReviewAdmissionCommand command, CancellationToken cancellationToken = default);
}
