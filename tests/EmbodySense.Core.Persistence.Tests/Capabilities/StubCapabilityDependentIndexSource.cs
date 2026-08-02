using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;

namespace EmbodySense.Core.Persistence.Tests.Capabilities;

internal sealed class StubCapabilityDependentIndexSource : ICapabilityDependentIndexSource
{
    internal IReadOnlyList<CapabilityDependent> Dependents { get; set; } = [];
    internal Exception? Failure { get; set; }
    public string Name => "stub";

    public Task<IReadOnlyList<CapabilityDependent>> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Failure is null ? Task.FromResult(Dependents) : Task.FromException<IReadOnlyList<CapabilityDependent>>(Failure);
    }
}
