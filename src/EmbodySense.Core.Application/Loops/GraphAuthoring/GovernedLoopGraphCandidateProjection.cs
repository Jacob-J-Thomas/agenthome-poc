using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Application.Loops.GraphAuthoring;

internal static class GovernedLoopGraphCandidateProjection
{
    internal static GovernedLoopGraphCandidate FromDefinition(GovernedLoopGraphDefinition graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return new GovernedLoopGraphCandidate(
            graph.SchemaVersion,
            graph.GraphId,
            graph.RevisionId,
            graph.Purpose,
            graph.OwningRole,
            graph.EntryNodeId,
            graph.TerminalNodeIds.Cast<string?>().ToArray(),
            graph.AuthorityCeiling,
            graph.ValueSchemas.Cast<GovernedLoopValueSchemaDefinition?>().ToArray(),
            graph.Nodes.Cast<GovernedLoopNodeDefinition?>().ToArray(),
            graph.ControlEdges.Cast<GovernedLoopControlEdgeDefinition?>().ToArray(),
            graph.Bindings.Cast<GovernedLoopBindingDefinition?>().ToArray(),
            graph.OutputContract,
            graph.DisplayMetadata);
    }

    internal static GovernedLoopGraphCandidate CopyAsRevision(
        GovernedLoopGraphDefinition source,
        GovernedLoopRevisionReference revision)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(revision);
        var candidate = FromDefinition(source);
        return candidate with
        {
            GraphId = revision.GraphId,
            RevisionId = revision.RevisionId,
        };
    }
}
