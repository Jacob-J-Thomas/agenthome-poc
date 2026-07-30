using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Common.Loops.Models;

namespace EmbodySense.Core.Application.Loops;

/// <summary>
/// Persists and retrieves generic loop definitions.
/// </summary>
public interface ILoopDefinitionStore
{
    /// <summary>
    /// Creates or replaces a loop definition.
    /// </summary>
    /// <param name="definition">The definition.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task SaveAsync(LoopDefinition definition, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads a loop definition by identifier.
    /// </summary>
    /// <param name="loopId">The loop ID.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The definition, or <see langword="null"/> when it is unknown.</returns>
    Task<LoopDefinition?> LoadAsync(string loopId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all persisted loop definitions.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The definitions in the store's deterministic order.</returns>
    Task<IReadOnlyList<LoopDefinition>> ListAsync(CancellationToken cancellationToken = default);
}
