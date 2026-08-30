namespace EmbodySense.Core.Startup.Runtime.Models;

/// <summary>Returns one current authenticated-operator decision for coordinator repair.</summary>
/// <param name="Status">The closed current-operator authority disposition.</param>
/// <param name="ActorId">The authenticated current operator when the status is ready or denied.</param>
public sealed record AgentRuntimeGovernedLoopCoordinatorRepairAuthority(
    AgentRuntimeGovernedLoopCoordinatorRepairAuthorityStatus Status,
    string? ActorId);
