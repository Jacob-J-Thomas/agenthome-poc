using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Common.Tests.Loops.Revisions;

public sealed class GovernedLoopGraphRevisionContractHashTests
{
    [Fact]
    public void Layout_hash_is_deterministic_canonical_and_independent_of_input_order()
    {
        var display = GovernedLoopGraphTestFixture.Display();
        var reorderedDisplay = display with { Nodes = display.Nodes.Reverse().ToArray() };
        var original = GovernedLoopGraphTestFixture.Create(display: display);
        var reordered = GovernedLoopGraphTestFixture.Create(display: reorderedDisplay);

        var originalHash = GovernedLoopGraphRevisionContractHash.ComputeLayoutHash(original);
        var reorderedHash = GovernedLoopGraphRevisionContractHash.ComputeLayoutHash(reordered);

        Assert.Equal(originalHash, reorderedHash);
        Assert.Equal(64, originalHash.Length);
        Assert.All(originalHash, character => Assert.True(character is >= '0' and <= '9' or >= 'a' and <= 'f'));
    }

    [Fact]
    public void Schema_one_canonical_layout_and_full_hashes_are_stable()
    {
        var artifact = Artifact(GovernedLoopGraphTestFixture.Create());

        Assert.Equal("c7ebd67190f910cda7016b2db89d3a69124b5333a638d8075ed0bcb42c59de35", artifact.LayoutHash);
        Assert.Equal("31ca1e9ff71c03a116f1506b4ff758e0fcc00218b1b123da48937dbbd6c4dafb", artifact.ArtifactHash);
    }

    [Fact]
    public void Layout_hash_binds_every_display_field_and_preserves_null_coordinates_distinct_from_zero()
    {
        var baselineDisplay = GovernedLoopGraphTestFixture.Display();
        var baseline = GovernedLoopGraphTestFixture.Create(display: baselineDisplay);
        var first = baselineDisplay.Nodes[0];
        var variants = new[]
        {
            baselineDisplay with { DisplayName = "Alternate name" },
            baselineDisplay with { Description = "Alternate description." },
            ReplaceFirst(baselineDisplay, first with { NodeId = "infer" }, removeNodeId: "infer"),
            ReplaceFirst(baselineDisplay, first with { DisplayName = "Alternate node name" }),
            ReplaceFirst(baselineDisplay, first with { Description = "Alternate node description." }),
            ReplaceFirst(baselineDisplay, first with { CanvasX = null }),
            ReplaceFirst(baselineDisplay, first with { CanvasY = null })
        };
        var baselineHash = GovernedLoopGraphRevisionContractHash.ComputeLayoutHash(baseline);

        foreach (var variant in variants)
        {
            var graph = GovernedLoopGraphTestFixture.Create(display: variant);
            Assert.NotEqual(baselineHash, GovernedLoopGraphRevisionContractHash.ComputeLayoutHash(graph));
        }

        var nullCoordinates = GovernedLoopGraphTestFixture.Create(
            display: ReplaceFirst(baselineDisplay, first with { CanvasX = null, CanvasY = null }));
        var zeroCoordinates = GovernedLoopGraphTestFixture.Create(
            display: ReplaceFirst(baselineDisplay, first with { CanvasX = 0, CanvasY = 0 }));
        Assert.NotEqual(
            GovernedLoopGraphRevisionContractHash.ComputeLayoutHash(nullCoordinates),
            GovernedLoopGraphRevisionContractHash.ComputeLayoutHash(zeroCoordinates));
    }

    [Fact]
    public void Layout_hash_excludes_executable_and_revision_identity_while_full_hash_binds_them()
    {
        var originalGraph = GovernedLoopGraphTestFixture.Create();
        var changedExecutable = GovernedLoopGraphTestFixture.Create(revisionId: "revision-2", purpose: "Changed executable purpose.");
        var original = Artifact(originalGraph);
        var changed = Artifact(changedExecutable);

        Assert.Equal(original.LayoutHash, changed.LayoutHash);
        Assert.NotEqual(original.Graph.ExecutableHash, changed.Graph.ExecutableHash);
        Assert.NotEqual(original.ArtifactHash, changed.ArtifactHash);
    }

    [Fact]
    public void Full_hash_binds_generic_revision_provenance_even_when_graph_content_is_identical()
    {
        var graph = GovernedLoopGraphTestFixture.Create();
        var first = Artifact(graph, actorId: "actor-1");
        var second = Artifact(graph, actorId: "actor-2");

        Assert.Equal(first.LayoutHash, second.LayoutHash);
        Assert.Equal(first.Graph.ExecutableHash, second.Graph.ExecutableHash);
        Assert.NotEqual(first.ArtifactHash, second.ArtifactHash);
        Assert.NotEqual(first.ArtifactHash, first.LayoutHash);
        Assert.NotEqual(first.ArtifactHash, GovernedLoopRevisionContractHash.ComputeArtifactHash(first.RevisionArtifact));
    }

    [Fact]
    public void Public_hashing_recomputes_the_exact_factory_digest()
    {
        var graph = GovernedLoopGraphTestFixture.Create();
        var artifact = Artifact(graph);

        Assert.Equal(artifact.ArtifactHash, GovernedLoopGraphRevisionContractHash.ComputeArtifactHash(artifact));
        Assert.Equal(artifact.LayoutHash, GovernedLoopGraphRevisionContractHash.ComputeLayoutHash(artifact.Graph));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopGraphRevisionContractHash.ComputeLayoutHash(null!));
    }

    private static GovernedLoopGraphRevisionArtifact Artifact(
        GovernedLoopGraphDefinition graph,
        string actorId = "actor-1")
    {
        var revision = GovernedLoopRevisionArtifactFactory.Create(
            1,
            graph.RevisionReference,
            null,
            null,
            "create-1",
            actorId,
            GovernedLoopRevisionTestFixture.CreatedAtUtc);
        return GovernedLoopGraphRevisionArtifactFactory.Create(1, revision, graph);
    }

    private static GovernedLoopDisplayMetadata ReplaceFirst(
        GovernedLoopDisplayMetadata display,
        GovernedLoopNodeDisplayMetadata replacement,
        string? removeNodeId = null)
    {
        var nodes = display.Nodes.Skip(1).Where(node => !string.Equals(node.NodeId, removeNodeId, StringComparison.Ordinal)).Prepend(replacement).ToArray();
        return display with { Nodes = nodes };
    }
}
