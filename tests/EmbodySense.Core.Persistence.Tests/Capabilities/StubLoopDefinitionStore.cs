using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Common.Loops;

namespace EmbodySense.Core.Persistence.Tests.Capabilities;

internal sealed class StubLoopDefinitionStore : ILoopDefinitionStore
{
    public IReadOnlyList<LoopDefinition> Definitions { get; init; } = [];

    public Task SaveAsync(LoopDefinition definition, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<LoopDefinition?> LoadAsync(string loopId, CancellationToken cancellationToken = default) => Task.FromResult(Definitions.SingleOrDefault(definition => definition.Id == loopId));

    public Task<IReadOnlyList<LoopDefinition>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult(Definitions);
}
