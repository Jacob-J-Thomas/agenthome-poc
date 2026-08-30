using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Runtime.Models;

namespace EmbodySense.Core.Startup.Tests.Runtime;

internal sealed class GovernedLoopCoordinatorRepairTestAuthorityProvider(
    string actorId,
    AgentRuntimeGovernedLoopCoordinatorRepairAuthorityStatus status = AgentRuntimeGovernedLoopCoordinatorRepairAuthorityStatus.Ready)
    : IAgentRuntimeGovernedLoopCoordinatorRepairAuthorityProvider
{
    private readonly string _actorId = actorId;
    private readonly AgentRuntimeGovernedLoopCoordinatorRepairAuthorityStatus _status = status;

    public Task<AgentRuntimeGovernedLoopCoordinatorRepairAuthority> ReadCurrentAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new AgentRuntimeGovernedLoopCoordinatorRepairAuthority(_status, _actorId));
    }
}
