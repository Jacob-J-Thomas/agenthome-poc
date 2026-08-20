using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Application.Loops.Sequential.Models;

/// <summary>Represents one builder-issued deterministic topology execution plan for an exact immutable graph artifact.</summary>
public sealed class GovernedLoopSequentialPlan
{
    internal GovernedLoopSequentialPlan(
        int schemaVersion,
        GovernedLoopRevisionReference revision,
        string graphArtifactHash,
        string graphLayoutHash,
        IReadOnlyList<GovernedLoopSequentialPlanNode> nodes,
        IReadOnlyList<GovernedLoopControlEdgeDefinition> controlEdges,
        IReadOnlyList<GovernedLoopTopologyComponent> components,
        GovernedLoopTopologySchedulerPolicy schedulerPolicy)
    {
        SchemaVersion = schemaVersion;
        Revision = revision;
        GraphArtifactHash = graphArtifactHash;
        GraphLayoutHash = graphLayoutHash;
        Nodes = nodes;
        ControlEdges = controlEdges;
        Components = components;
        SchedulerPolicy = schedulerPolicy;
    }

    /// <summary>Gets the plan schema version, which is always 1.</summary>
    public int SchemaVersion { get; }

    /// <summary>Gets the exact immutable executable graph revision.</summary>
    public GovernedLoopRevisionReference Revision { get; }

    /// <summary>Gets the exact immutable full graph-artifact hash.</summary>
    public string GraphArtifactHash { get; }

    /// <summary>Gets the exact immutable graph-layout hash.</summary>
    public string GraphLayoutHash { get; }

    /// <summary>Gets the read-only nodes in stable condensed-topology order.</summary>
    public IReadOnlyList<GovernedLoopSequentialPlanNode> Nodes { get; }

    /// <summary>Gets every exact admitted control edge in canonical identity order.</summary>
    public IReadOnlyList<GovernedLoopControlEdgeDefinition> ControlEdges { get; }

    /// <summary>Gets the stable strongly connected components in deterministic condensed-topology order.</summary>
    public IReadOnlyList<GovernedLoopTopologyComponent> Components { get; }

    /// <summary>Gets the immutable bounded deterministic scheduling policy.</summary>
    public GovernedLoopTopologySchedulerPolicy SchedulerPolicy { get; }
}
