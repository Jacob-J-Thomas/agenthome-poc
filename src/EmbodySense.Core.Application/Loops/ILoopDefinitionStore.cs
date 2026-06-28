using EmbodySense.Core.Application.Loops.Models;

namespace EmbodySense.Core.Application.Loops;

public interface ILoopDefinitionStore
{
    Task SaveAsync(LoopDefinition definition, CancellationToken cancellationToken = default);

    Task<LoopDefinition?> LoadAsync(string loopId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LoopDefinition>> ListAsync(CancellationToken cancellationToken = default);
}
