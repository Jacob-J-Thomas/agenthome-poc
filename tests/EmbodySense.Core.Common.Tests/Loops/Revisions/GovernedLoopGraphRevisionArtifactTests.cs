using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Common.Tests.Loops.Revisions;

public sealed class GovernedLoopGraphRevisionArtifactTests
{
    [Fact]
    public void Factory_composes_deeply_snapshotted_schema_one_graph_and_lineage()
    {
        var graph = GovernedLoopGraphTestFixture.Create();
        var revisionArtifact = RevisionArtifact(graph);

        var artifact = GovernedLoopGraphRevisionArtifactFactory.Create(1, revisionArtifact, graph);

        Assert.Equal(GovernedLoopGraphRevisionArtifact.CurrentSchemaVersion, artifact.SchemaVersion);
        Assert.Equal(graph.RevisionReference, artifact.RevisionArtifact.Revision);
        Assert.Equal(graph.GraphId, artifact.Graph.GraphId);
        Assert.Equal(graph.RevisionId, artifact.Graph.RevisionId);
        Assert.Equal(graph.ExecutableHash, artifact.Graph.ExecutableHash);
        Assert.NotSame(revisionArtifact, artifact.RevisionArtifact);
        Assert.NotSame(revisionArtifact.Revision, artifact.RevisionArtifact.Revision);
        Assert.NotSame(graph, artifact.Graph);
        Assert.NotSame(graph.DisplayMetadata, artifact.Graph.DisplayMetadata);
        Assert.Equal(artifact.LayoutHash, GovernedLoopGraphRevisionContractHash.ComputeLayoutHash(artifact.Graph));
        Assert.Equal(artifact.ArtifactHash, GovernedLoopGraphRevisionContractHash.ComputeArtifactHash(artifact));
        AssertCanonicalHash(artifact.LayoutHash);
        AssertCanonicalHash(artifact.ArtifactHash);
    }

    [Fact]
    public void Factory_rejects_unsupported_schema_nulls_malformed_lineage_and_reference_substitution()
    {
        var graph = GovernedLoopGraphTestFixture.Create();
        var validArtifact = RevisionArtifact(graph);
        var mismatchedGraph = GovernedLoopGraphTestFixture.Create(graphId: "other-graph");
        var malformedArtifact = new GovernedLoopRevisionArtifact(
            1,
            graph.RevisionReference,
            graph.RevisionReference,
            null,
            "replace-1",
            "actor-1",
            GovernedLoopRevisionTestFixture.CreatedAtUtc);

        Assert.Throws<ArgumentException>(() => GovernedLoopGraphRevisionArtifactFactory.Create(2, validArtifact, graph));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopGraphRevisionArtifactFactory.Create(1, null!, graph));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopGraphRevisionArtifactFactory.Create(1, validArtifact, null!));
        Assert.Throws<ArgumentException>(() => GovernedLoopGraphRevisionArtifactFactory.Create(1, malformedArtifact, graph));
        Assert.Throws<ArgumentException>(() => GovernedLoopGraphRevisionArtifactFactory.Create(1, validArtifact, mismatchedGraph));
    }

    [Fact]
    public void Factory_rejects_same_graph_and_revision_when_executable_content_differs()
    {
        var graph = GovernedLoopGraphTestFixture.Create();
        var changedGraph = GovernedLoopGraphTestFixture.Create(purpose: "Execute materially different content.");
        var revisionArtifact = RevisionArtifact(graph);

        Assert.Equal(graph.GraphId, changedGraph.GraphId);
        Assert.Equal(graph.RevisionId, changedGraph.RevisionId);
        Assert.NotEqual(graph.ExecutableHash, changedGraph.ExecutableHash);
        Assert.Throws<ArgumentException>(() => GovernedLoopGraphRevisionArtifactFactory.Create(1, revisionArtifact, changedGraph));
    }

    [Fact]
    public void Layout_only_successor_preserves_executable_identity_but_changes_layout_and_full_identity()
    {
        var originalGraph = GovernedLoopGraphTestFixture.Create();
        var changedDisplay = originalGraph.DisplayMetadata with { DisplayName = "Research loop rearranged" };
        var successorGraph = GovernedLoopGraphTestFixture.Create(revisionId: "revision-2", display: changedDisplay);
        var original = GovernedLoopGraphRevisionArtifactFactory.Create(1, RevisionArtifact(originalGraph), originalGraph);
        var successorLineage = GovernedLoopRevisionArtifactFactory.Create(
            1,
            successorGraph.RevisionReference,
            originalGraph.RevisionReference,
            null,
            "replace-1",
            "actor-1",
            GovernedLoopRevisionTestFixture.CreatedAtUtc.AddMinutes(1));

        var successor = GovernedLoopGraphRevisionArtifactFactory.Create(1, successorLineage, successorGraph);

        Assert.Equal(original.Graph.ExecutableHash, successor.Graph.ExecutableHash);
        Assert.NotEqual(original.Graph.RevisionId, successor.Graph.RevisionId);
        Assert.NotEqual(original.LayoutHash, successor.LayoutHash);
        Assert.NotEqual(original.ArtifactHash, successor.ArtifactHash);
        Assert.Equal(original.RevisionArtifact.Revision, successor.RevisionArtifact.PredecessorRevision);
    }

    [Fact]
    public void Rollback_successor_can_compose_exact_historical_executable_with_new_revision_identity()
    {
        var graph = GovernedLoopGraphTestFixture.Create(revisionId: "revision-3");
        var historicalReference = GovernedLoopRevisionReference.Create(1, graph.GraphId, "revision-1", graph.ExecutableHash);
        var historicalPublication = GovernedLoopRevisionPublicationPinFactory.Create(
            1,
            historicalReference,
            "publish-historical",
            GovernedLoopRevisionTestFixture.ValidationHash);
        var predecessor = GovernedLoopRevisionReference.Create(1, graph.GraphId, "revision-2", new string('e', 64));
        var rollbackLineage = GovernedLoopRevisionArtifactFactory.Create(
            1,
            graph.RevisionReference,
            predecessor,
            historicalPublication,
            "rollback-1",
            "actor-1",
            GovernedLoopRevisionTestFixture.CreatedAtUtc);

        var artifact = GovernedLoopGraphRevisionArtifactFactory.Create(1, rollbackLineage, graph);

        Assert.Equal(historicalReference.ExecutableHash, artifact.Graph.ExecutableHash);
        Assert.Equal(historicalPublication, artifact.RevisionArtifact.RollbackSourcePublication);
        Assert.Equal("revision-3", artifact.Graph.RevisionId);
    }

    private static GovernedLoopRevisionArtifact RevisionArtifact(
        GovernedLoopGraphDefinition graph,
        string operationId = "create-1",
        string actorId = "actor-1")
        => GovernedLoopRevisionArtifactFactory.Create(
            1,
            graph.RevisionReference,
            null,
            null,
            operationId,
            actorId,
            GovernedLoopRevisionTestFixture.CreatedAtUtc);

    private static void AssertCanonicalHash(string value)
    {
        Assert.Equal(64, value.Length);
        Assert.All(value, character => Assert.True(character is >= '0' and <= '9' or >= 'a' and <= 'f'));
    }
}
