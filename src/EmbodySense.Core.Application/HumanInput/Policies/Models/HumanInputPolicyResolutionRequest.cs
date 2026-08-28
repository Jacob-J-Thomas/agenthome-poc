using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Application.HumanInput.Policies.Models;

/// <summary>Supplies only server-derived graph and actor coordinates for exact Human Input policy resolution.</summary>
/// <param name="WorkspaceId">The trusted workspace identity.</param>
/// <param name="GraphId">The trusted immutable graph identity.</param>
/// <param name="GraphRevisionId">The trusted immutable graph-revision identity.</param>
/// <param name="NodeId">The trusted Human Input node identity.</param>
/// <param name="ActorId">The trusted actor identity requesting admission.</param>
/// <param name="Configuration">The exact admitted Human Input node configuration containing two immutable policy references.</param>
public sealed record HumanInputPolicyResolutionRequest(string WorkspaceId, string GraphId, string GraphRevisionId, string NodeId, string ActorId, GovernedLoopHumanInputNodeConfiguration Configuration);
