using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Common.Loops.Models;

namespace EmbodySense.Core.Application.Loops;

/// <summary>
/// Persists and retrieves generic loop-run evidence.
/// </summary>
public interface ILoopRunStore
{
    /// <summary>
    /// Creates or replaces a run record.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task SaveAsync(LoopRunRecord run, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads a run by loop and run identifiers.
    /// </summary>
    /// <param name="loopId">The loop ID.</param>
    /// <param name="runId">The run ID.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The run, or <see langword="null"/> when it is unknown.</returns>
    Task<LoopRunRecord?> LoadAsync(string loopId, string runId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the retained runs for one loop.
    /// </summary>
    /// <param name="loopId">The loop ID.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The retained runs in the store's deterministic order.</returns>
    Task<IReadOnlyList<LoopRunRecord>> ListAsync(string loopId, CancellationToken cancellationToken = default);
}
