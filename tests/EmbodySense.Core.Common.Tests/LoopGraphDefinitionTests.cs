using EmbodySense.Core.Common.Loops.Models;

namespace EmbodySense.Core.Common.Tests;

public sealed class LoopGraphDefinitionTests
{
    [Fact]
    public void Default_conversation_graph_is_valid_and_system_locked()
    {
        var graph = LoopGraphDefinition.CreateDefaultConversation();

        Assert.Null(graph.GetValidationFailure());
        Assert.Equal(DefaultConversationLoopGraphIds.AcceptUserMessage, graph.EntryNodeId);
        Assert.Equal([DefaultConversationLoopGraphIds.CompleteRun], graph.TerminalNodeIds);
        Assert.All(graph.Nodes, node => Assert.Equal(LoopGraphNodeEditMode.SystemLocked, node.EditMode));
        Assert.Contains(graph.Nodes, node => node.Id == DefaultConversationLoopGraphIds.DispatchInference && node.Kind == LoopGraphNodeKind.ModelInference);
    }

    [Theory]
    [MemberData(nameof(InvalidGraphs))]
    public void Validation_reports_first_invalid_graph_condition(LoopGraphDefinition graph, string expectedMessage)
    {
        var failure = graph.GetValidationFailure();

        Assert.NotNull(failure);
        Assert.Contains(expectedMessage, failure, StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> InvalidGraphs()
    {
        var graph = LoopGraphDefinition.CreateDefaultConversation();
        var firstNode = graph.Nodes[0];
        var firstEdge = graph.Edges[0];

        yield return [graph with { EntryNodeId = "" }, "entry node id"];
        yield return [graph with { TerminalNodeIds = [] }, "terminal node id"];
        yield return [graph with { Nodes = [] }, "at least one node"];
        yield return [graph with { Nodes = [firstNode, firstNode] }, "duplicate node id"];
        yield return [graph with { TerminalNodeIds = ["missing-terminal"] }, "terminal node `missing-terminal` does not exist"];
        yield return [graph with { Nodes = [firstNode with { Id = "" }] }, "nodes must include ids"];
        yield return [graph with { Nodes = [firstNode with { DisplayName = "" }] }, "display name"];
        yield return [graph with { Nodes = [firstNode with { Description = "" }] }, "description"];
        yield return [graph with { Nodes = [firstNode with { Kind = LoopGraphNodeKind.Unknown }] }, "unsupported kind"];
        yield return [graph with { Nodes = [firstNode with { EditMode = LoopGraphNodeEditMode.Unknown }] }, "unsupported edit mode"];
        yield return [graph with { Nodes = [firstNode with { CapabilityIds = [""] }] }, "capability ids"];
        yield return [graph with { Edges = null! }, "edge list cannot be null"];
        yield return [graph with { Edges = [firstEdge, firstEdge] }, "duplicate edge id"];
        yield return [graph with { Edges = [firstEdge with { Id = "" }] }, "edges must include ids"];
        yield return [graph with { Edges = [firstEdge with { FromNodeId = "missing-from" }] }, "missing from node"];
        yield return [graph with { Edges = [firstEdge with { ToNodeId = "missing-to" }] }, "missing to node"];
        yield return [graph with { Edges = [firstEdge with { Condition = LoopGraphEdgeCondition.Unknown }] }, "unsupported condition"];
        yield return [graph with { Edges = [firstEdge with { Description = "" }] }, "must include a description"];
    }
}
