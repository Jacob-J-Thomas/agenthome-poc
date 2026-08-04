using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Common.Tests;

public sealed class GovernedLoopExecutableHashTests
{
    [Fact]
    public void Canonical_schema_one_hash_is_pinned()
    {
        Assert.Equal("7070f926faefd98b1ccd517e6a1a8b0070f6a3385ec224f6db157cf4ea45b0dc", GovernedLoopGraphTestFixture.Create().ExecutableHash);
    }

    [Fact]
    public void Hash_requires_a_validated_graph()
    {
        Assert.Throws<ArgumentNullException>(() => GovernedLoopExecutableHash.Compute(null!));
    }

    [Fact]
    public void Hash_supports_every_defined_value_and_control_token()
    {
        var schemas = new[]
        {
            new GovernedLoopValueSchemaDefinition("text", GovernedLoopValueKind.Text, false),
            new GovernedLoopValueSchemaDefinition("boolean", GovernedLoopValueKind.Boolean, false),
            new GovernedLoopValueSchemaDefinition("integer", GovernedLoopValueKind.Integer, false),
            new GovernedLoopValueSchemaDefinition("number", GovernedLoopValueKind.Number, false),
            new GovernedLoopValueSchemaDefinition("object", GovernedLoopValueKind.Object, false),
            new GovernedLoopValueSchemaDefinition("binary", GovernedLoopValueKind.Binary, false),
            new GovernedLoopValueSchemaDefinition("array", GovernedLoopValueKind.Array, false, ElementSchemaId: "text")
        };
        Assert.Equal(64, GovernedLoopGraphTestFixture.Create(schemas: schemas).ExecutableHash.Length);

        foreach (var condition in Enum.GetValues<GovernedLoopControlCondition>().Where(condition => condition != GovernedLoopControlCondition.Unknown))
        {
            var edges = GovernedLoopGraphTestFixture.Edges();
            edges[0] = edges[0] with { Condition = condition };
            Assert.Equal(64, GovernedLoopGraphTestFixture.Create(edges: edges).ExecutableHash.Length);
        }
    }

    [Fact]
    public void Hash_is_canonical_across_input_collection_order()
    {
        var expected = GovernedLoopGraphTestFixture.Create();

        var reordered = GovernedLoopGraphTestFixture.Create(
            schemas: GovernedLoopGraphTestFixture.Schemas().Reverse(),
            nodes: GovernedLoopGraphTestFixture.Nodes().Reverse(),
            edges: GovernedLoopGraphTestFixture.Edges().Reverse(),
            bindings: GovernedLoopGraphTestFixture.Bindings().Reverse());

        Assert.Equal(expected.ExecutableHash, reordered.ExecutableHash);
        Assert.Equal(expected.Nodes.Select(node => node.Id).Order(StringComparer.Ordinal), reordered.Nodes.Select(node => node.Id));
    }

    [Fact]
    public void Hash_is_property_stable_across_randomized_collection_permutations()
    {
        var expected = GovernedLoopGraphTestFixture.Create().ExecutableHash;
        for (var seed = 0; seed < 64; seed++)
        {
            var random = new Random(seed);
            var graph = GovernedLoopGraphTestFixture.Create(
                schemas: GovernedLoopGraphTestFixture.Schemas().OrderBy(_ => random.Next()),
                nodes: GovernedLoopGraphTestFixture.Nodes().OrderBy(_ => random.Next()),
                edges: GovernedLoopGraphTestFixture.Edges().OrderBy(_ => random.Next()),
                bindings: GovernedLoopGraphTestFixture.Bindings().OrderBy(_ => random.Next()));
            Assert.Equal(expected, graph.ExecutableHash);
        }
    }

    [Fact]
    public void Display_layout_graph_identity_and_revision_identity_do_not_affect_executable_hash()
    {
        var expected = GovernedLoopGraphTestFixture.Create();
        var changed = GovernedLoopGraphTestFixture.Create(
            graphId: "another-loop",
            revisionId: "revision-99",
            display: GovernedLoopGraphTestFixture.Display("A completely different label", -500, 700));

        Assert.Equal(expected.ExecutableHash, changed.ExecutableHash);
        Assert.NotEqual(expected.RevisionReference, changed.RevisionReference);
    }

    [Fact]
    public void Every_executable_contract_slice_changes_the_hash()
    {
        var expected = GovernedLoopGraphTestFixture.Create().ExecutableHash;
        var nodes = GovernedLoopGraphTestFixture.Nodes();
        nodes[1] = nodes[1] with { Parameters = new Dictionary<string, string> { ["instruction"] = "Use a different bounded instruction." } };

        Assert.NotEqual(expected, GovernedLoopGraphTestFixture.Create(purpose: "Perform a different governed purpose.").ExecutableHash);
        Assert.NotEqual(expected, GovernedLoopGraphTestFixture.Create(authorityCeiling: GovernedLoopAuthorityCeiling.Create(["model-inference"]), nodes: nodes.Select(node => node.Id == "infer" ? node with { AuthorityCeiling = GovernedLoopAuthorityCeiling.Create(["model-inference"]) } : node)).ExecutableHash);
        Assert.NotEqual(expected, GovernedLoopGraphTestFixture.Create(nodes: nodes).ExecutableHash);
    }

    [Fact]
    public void Canonical_snapshots_do_not_change_when_caller_collections_mutate()
    {
        var nodes = GovernedLoopGraphTestFixture.Nodes();
        var bindings = GovernedLoopGraphTestFixture.Bindings();
        var graph = GovernedLoopGraphTestFixture.Create(nodes: nodes, bindings: bindings);
        var hash = graph.ExecutableHash;

        nodes[1] = nodes[1] with { Id = "mutated" };
        bindings[0] = bindings[0] with { ToPortId = "mutated" };

        Assert.Equal(hash, graph.ExecutableHash);
        Assert.Contains(graph.Nodes, node => node.Id == "infer");
        Assert.Equal("request", graph.Bindings.Single(binding => binding.Id == "request-binding").ToPortId);
    }

    [Theory]
    [InlineData("ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef0123456789")]
    [InlineData("short")]
    [InlineData("000000000000000000000000000000000000000000000000000000000000000g")]
    public void Revision_reference_rejects_noncanonical_hashes(string hash)
    {
        Assert.Throws<ArgumentException>(() => GovernedLoopRevisionReference.Create(GovernedLoopRevisionReference.CurrentSchemaVersion, "graph", "revision-1", hash));
    }

    [Fact]
    public void Revision_reference_rejects_non_schema_one_contracts()
    {
        Assert.Throws<ArgumentException>(() => GovernedLoopRevisionReference.Create(2, "graph", "revision-1", new string('0', 64)));
    }
}
