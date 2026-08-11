using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Sequential;

internal static class GovernedLoopSequentialApplicationTestFixture
{
    internal static readonly DateTimeOffset Now = new(2026, 8, 10, 22, 0, 0, TimeSpan.Zero);

    internal static GovernedLoopGraphRevisionArtifact LinearArtifact(
        int inferenceCount = 1,
        IReadOnlyList<string>? inferenceIds = null,
        Func<int, GovernedLoopNodeDescriptor>? inferenceDescriptor = null,
        ContextualRoleRevisionPin? owningRole = null)
    {
        inferenceIds ??= Enumerable.Range(1, inferenceCount).Select(index => $"infer-{index:D2}").ToArray();
        if (inferenceIds.Count != inferenceCount)
        {
            throw new ArgumentException("Inference identities must match the requested count.", nameof(inferenceIds));
        }

        var nodes = new List<GovernedLoopNodeDefinition>
        {
            Node("trigger", GovernedLoopSequentialNodeDescriptors.ManualTrigger),
        };
        nodes.AddRange(inferenceIds.Select((id, index) => Node(id, inferenceDescriptor?.Invoke(index) ?? GovernedLoopSequentialNodeDescriptors.ProviderInference)));
        nodes.Add(Exit("exit"));

        var executionOrder = new[] { "trigger" }.Concat(inferenceIds).Append("exit").ToArray();
        var edges = executionOrder.Zip(executionOrder.Skip(1), (from, to) => new GovernedLoopControlEdgeDefinition(
            $"{from}-to-{to}",
            from,
            to,
            string.Equals(from, "trigger", StringComparison.Ordinal) ? GovernedLoopControlCondition.Always : GovernedLoopControlCondition.Success)).ToArray();
        return Artifact(nodes, edges, ["exit"], owningRole);
    }

    internal static GovernedLoopGraphRevisionArtifact Artifact(
        IReadOnlyList<GovernedLoopNodeDefinition> nodes,
        IReadOnlyList<GovernedLoopControlEdgeDefinition> edges,
        IReadOnlyList<string> terminalNodeIds,
        ContextualRoleRevisionPin? owningRole = null)
    {
        owningRole ??= new ContextualRoleRevisionPin(new ContextualRoleRevisionIdentity("sequential-role", 1), Hash('a'));
        var graph = GovernedLoopGraphDefinition.Create(
            1,
            "sequential-loop",
            "revision-1",
            "Execute one exact supported sequential governed graph.",
            owningRole,
            "trigger",
            terminalNodeIds,
            GovernedLoopAuthorityCeiling.Create([]),
            [new GovernedLoopValueSchemaDefinition("text", GovernedLoopValueKind.Text, false)],
            nodes,
            edges,
            [],
            new GovernedLoopOutputContract("Return the exact bounded result.", [new GovernedLoopOutputDefinition("result", "text", terminalNodeIds[0], "published-result", true)]),
            new GovernedLoopDisplayMetadata(
                "Sequential loop",
                "Display metadata is not execution order.",
                nodes.Select((node, index) => new GovernedLoopNodeDisplayMetadata(node.Id, node.Id, "Node.", index * 100, 0)).ToArray()));
        var revision = GovernedLoopRevisionArtifactFactory.Create(1, graph.RevisionReference, null, null, "create-sequential", "user-owner", Now);
        return GovernedLoopGraphRevisionArtifactFactory.Create(1, revision, graph);
    }

    internal static GovernedLoopNodeDefinition Node(string id, GovernedLoopNodeDescriptor descriptor)
        => new(id, descriptor, [], GovernedLoopAuthorityCeiling.Create([]), new Dictionary<string, string>());

    internal static GovernedLoopNodeDefinition Exit(string id)
        => new(
            id,
            GovernedLoopSequentialNodeDescriptors.SuccessExit,
            [new GovernedLoopPortDefinition("published-result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "text", true)],
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string>());

    internal static string Hash(char value) => new(value, 64);
}
