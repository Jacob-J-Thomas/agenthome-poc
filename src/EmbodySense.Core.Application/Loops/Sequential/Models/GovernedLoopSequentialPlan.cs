using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Application.Loops.Sequential.Models;

/// <summary>Represents one builder-issued deterministic linear execution plan for an exact immutable graph artifact.</summary>
public sealed class GovernedLoopSequentialPlan
{
    internal GovernedLoopSequentialPlan(
        int schemaVersion,
        GovernedLoopRevisionReference revision,
        string graphArtifactHash,
        string graphLayoutHash,
        IReadOnlyList<GovernedLoopSequentialPlanNode> nodes)
    {
        SchemaVersion = schemaVersion;
        Revision = revision;
        GraphArtifactHash = graphArtifactHash;
        GraphLayoutHash = graphLayoutHash;
        Nodes = nodes;
    }

    /// <summary>Gets the plan schema version, which is always 1.</summary>
    public int SchemaVersion { get; }

    /// <summary>Gets the exact immutable executable graph revision.</summary>
    public GovernedLoopRevisionReference Revision { get; }

    /// <summary>Gets the exact immutable full graph-artifact hash.</summary>
    public string GraphArtifactHash { get; }

    /// <summary>Gets the exact immutable graph-layout hash.</summary>
    public string GraphLayoutHash { get; }

    /// <summary>Gets the read-only nodes in control-edge traversal order.</summary>
    public IReadOnlyList<GovernedLoopSequentialPlanNode> Nodes { get; }
}
