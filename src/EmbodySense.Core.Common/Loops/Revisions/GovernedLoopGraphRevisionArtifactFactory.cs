using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Common.Loops.Revisions;

/// <summary>Creates validated immutable graph-revision artifacts.</summary>
public static class GovernedLoopGraphRevisionArtifactFactory
{
    /// <summary>Creates one immutable artifact from exact lifecycle provenance and a canonical graph payload.</summary>
    /// <param name="schemaVersion">The schema version, which must be 1.</param>
    /// <param name="revisionArtifact">The exact revision lineage and creation provenance.</param>
    /// <param name="graph">The canonical graph payload for the same exact revision reference.</param>
    /// <returns>A deeply snapshotted artifact with canonical layout and full-content hashes.</returns>
    /// <exception cref="ArgumentNullException">Thrown when a required artifact or graph is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when the schema, nested contracts, or exact revision composition is invalid.</exception>
    public static GovernedLoopGraphRevisionArtifact Create(
        int schemaVersion,
        GovernedLoopRevisionArtifact revisionArtifact,
        GovernedLoopGraphDefinition graph)
    {
        if (schemaVersion != GovernedLoopGraphRevisionArtifact.CurrentSchemaVersion)
        {
            throw new ArgumentException($"Schema version must be {GovernedLoopGraphRevisionArtifact.CurrentSchemaVersion}; compatibility translation is not supported.", nameof(schemaVersion));
        }

        ArgumentNullException.ThrowIfNull(revisionArtifact);
        ArgumentNullException.ThrowIfNull(graph);

        var revisionCopy = GovernedLoopRevisionArtifactFactory.Create(
            revisionArtifact.SchemaVersion,
            revisionArtifact.Revision,
            revisionArtifact.PredecessorRevision,
            revisionArtifact.RollbackSourcePublication,
            revisionArtifact.CreationOperationId,
            revisionArtifact.CreatedByActorId,
            revisionArtifact.CreatedAtUtc);
        var graphCopy = CopyGraph(graph);

        if (!GovernedLoopRevisionContractGuard.IsSameReference(revisionCopy.Revision, graphCopy.RevisionReference))
        {
            throw new ArgumentException("The graph payload must match the revision artifact's exact graph, revision, and executable-content identity.", nameof(graph));
        }

        var layoutHash = GovernedLoopGraphRevisionContractHash.ComputeLayoutHash(graphCopy);
        var artifactHash = GovernedLoopGraphRevisionContractHash.ComputeArtifactHashCore(schemaVersion, revisionCopy, graphCopy, layoutHash);
        return new GovernedLoopGraphRevisionArtifact(schemaVersion, revisionCopy, graphCopy, layoutHash, artifactHash);
    }

    internal static void RequireValidComposition(GovernedLoopGraphRevisionArtifact artifact, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(artifact, parameterName);
        if (artifact.SchemaVersion != GovernedLoopGraphRevisionArtifact.CurrentSchemaVersion)
        {
            throw new ArgumentException($"Schema version must be {GovernedLoopGraphRevisionArtifact.CurrentSchemaVersion}; compatibility translation is not supported.", parameterName);
        }

        ArgumentNullException.ThrowIfNull(artifact.RevisionArtifact, parameterName);
        ArgumentNullException.ThrowIfNull(artifact.Graph, parameterName);
        GovernedLoopRevisionContractGuard.RequireValid(GovernedLoopRevisionContractValidator.Validate(artifact.RevisionArtifact), parameterName);
        if (!GovernedLoopRevisionContractGuard.IsSameReference(artifact.RevisionArtifact.Revision, artifact.Graph.RevisionReference))
        {
            throw new ArgumentException("The graph payload must match the revision artifact's exact graph, revision, and executable-content identity.", parameterName);
        }

        var expectedLayoutHash = GovernedLoopGraphRevisionContractHash.ComputeLayoutHash(artifact.Graph);
        if (!string.Equals(artifact.LayoutHash, expectedLayoutHash, StringComparison.Ordinal))
        {
            throw new ArgumentException("The graph-revision artifact contains a non-canonical layout hash.", parameterName);
        }

        var expectedArtifactHash = GovernedLoopGraphRevisionContractHash.ComputeArtifactHashCore(
            artifact.SchemaVersion,
            artifact.RevisionArtifact,
            artifact.Graph,
            expectedLayoutHash);
        if (!string.Equals(artifact.ArtifactHash, expectedArtifactHash, StringComparison.Ordinal))
        {
            throw new ArgumentException("The graph-revision artifact contains a non-canonical full-artifact hash.", parameterName);
        }
    }

    private static GovernedLoopGraphDefinition CopyGraph(GovernedLoopGraphDefinition graph)
        => GovernedLoopGraphDefinition.Create(
            graph.SchemaVersion,
            graph.GraphId,
            graph.RevisionId,
            graph.Purpose,
            graph.OwningRole,
            graph.EntryNodeId,
            graph.TerminalNodeIds,
            graph.AuthorityCeiling,
            graph.ValueSchemas,
            graph.Nodes,
            graph.ControlEdges,
            graph.Bindings,
            graph.OutputContract,
            graph.DisplayMetadata);
}
