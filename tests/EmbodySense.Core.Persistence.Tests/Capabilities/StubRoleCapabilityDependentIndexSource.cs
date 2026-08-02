using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;

namespace EmbodySense.Core.Persistence.Tests.Capabilities;

internal sealed class StubRoleCapabilityDependentIndexSource : IRoleCapabilityDependentIndexSource
{
    internal IReadOnlyList<CapabilityDependent> Dependents { get; init; } = [];
    public string Name => "roles";
    public Task<IReadOnlyList<CapabilityDependent>> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Dependents);
}
