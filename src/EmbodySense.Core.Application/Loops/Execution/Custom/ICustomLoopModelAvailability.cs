using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Execution.Custom;

/// <summary>
/// Determines whether an admitted provider/model snapshot can still be used for explicit resume.
/// </summary>
public interface ICustomLoopModelAvailability
{
    /// <summary>
    /// Determines whether the model snapshot is available asynchronously.
    /// </summary>
    /// <param name="modelSnapshot">The model snapshot.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns><see langword="true"/> when the exact provider/model remains available; otherwise, <see langword="false"/>.</returns>
    Task<bool> IsAvailableAsync(CustomLoopModelSnapshot modelSnapshot, CancellationToken cancellationToken = default);
}
