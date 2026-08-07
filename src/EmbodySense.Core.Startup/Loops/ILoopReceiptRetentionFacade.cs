using EmbodySense.Core.Startup.Loops.Models;

namespace EmbodySense.Core.Startup.Loops;

/// <summary>
/// Exposes safe receipt-retention posture and explicit bounded cleanup to interface hosts.
/// </summary>
public interface ILoopReceiptRetentionFacade
{
    /// <summary>
    /// Inspects the current safe workspace and per-class retention posture.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel inspection.</param>
    /// <returns>The safe retention posture.</returns>
    Task<LoopReceiptRetentionPostureSnapshot> GetPostureAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests one explicit, server-attributed, policy-bounded cleanup operation.
    /// </summary>
    /// <param name="input">The caller operation identity and bounded cleanup request.</param>
    /// <param name="cancellationToken">The token used to cancel cleanup.</param>
    /// <returns>The safe durable cleanup outcome.</returns>
    Task<LoopReceiptCleanupResponse> CleanupAsync(LoopReceiptCleanupInput input, CancellationToken cancellationToken = default);
}
