using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Sequential;

internal static class GovernedLoopSequentialApplicationTestFixture
{
    internal const string ModelInferenceCapabilityId = "org.embodysense/model-inference";

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
            Trigger("trigger"),
        };
        nodes.AddRange(inferenceIds.Select((id, index) => inferenceDescriptor is null
            ? Inference(id, $"Execute bounded inference step {index + 1}.")
            : Node(id, inferenceDescriptor(index))));
        nodes.Add(Exit("exit"));

        var executionOrder = new[] { "trigger" }.Concat(inferenceIds).Append("exit").ToArray();
        var edges = executionOrder.Zip(executionOrder.Skip(1), (from, to) => new GovernedLoopControlEdgeDefinition(
            $"{from}-to-{to}",
            from,
            to,
            string.Equals(from, "trigger", StringComparison.Ordinal) ? GovernedLoopControlCondition.Always : GovernedLoopControlCondition.Success)).ToArray();
        var bindings = new List<GovernedLoopBindingDefinition>();
        var dataSourceNodeId = "trigger";
        var dataSourcePortId = "request";
        foreach (var inferenceId in inferenceIds)
        {
            bindings.Add(new GovernedLoopBindingDefinition($"data-to-{inferenceId}", GovernedLoopBindingKind.Data, dataSourceNodeId, dataSourcePortId, inferenceId, "request"));
            bindings.Add(new GovernedLoopBindingDefinition($"context-to-{inferenceId}", GovernedLoopBindingKind.Context, "trigger", "invocation-context", inferenceId, "invocation-context"));
            dataSourceNodeId = inferenceId;
            dataSourcePortId = "result";
        }

        bindings.Add(new GovernedLoopBindingDefinition("result-to-exit", GovernedLoopBindingKind.Data, dataSourceNodeId, dataSourcePortId, "exit", "result"));
        return Artifact(nodes, edges, ["exit"], owningRole, bindings);
    }

    internal static GovernedLoopGraphRevisionArtifact Artifact(
        IReadOnlyList<GovernedLoopNodeDefinition> nodes,
        IReadOnlyList<GovernedLoopControlEdgeDefinition> edges,
        IReadOnlyList<string> terminalNodeIds,
        ContextualRoleRevisionPin? owningRole = null,
        IReadOnlyList<GovernedLoopBindingDefinition>? bindings = null,
        IReadOnlyList<GovernedLoopValueSchemaDefinition>? valueSchemas = null,
        GovernedLoopOutputContract? outputContract = null,
        GovernedLoopAuthorityCeiling? authorityCeiling = null)
    {
        owningRole ??= new ContextualRoleRevisionPin(new ContextualRoleRevisionIdentity("sequential-role", 1), Hash('a'));
        valueSchemas ??= [new GovernedLoopValueSchemaDefinition("text", GovernedLoopValueKind.Text, false)];
        outputContract ??= new GovernedLoopOutputContract("Return the exact bounded result.", [new GovernedLoopOutputDefinition("result", "text", terminalNodeIds[0], "published-result", true)]);
        authorityCeiling ??= GovernedLoopAuthorityCeiling.Create([ModelInferenceCapabilityId]);
        var graph = GovernedLoopGraphDefinition.Create(
            1,
            "sequential-loop",
            "revision-1",
            "Execute one exact supported sequential governed graph.",
            owningRole,
            "trigger",
            terminalNodeIds,
            authorityCeiling,
            valueSchemas,
            nodes,
            edges,
            bindings ?? [],
            outputContract,
            new GovernedLoopDisplayMetadata(
                "Sequential loop",
                "Display metadata is not execution order.",
                nodes.Select((node, index) => new GovernedLoopNodeDisplayMetadata(node.Id, node.Id, "Node.", index * 100, 0)).ToArray()));
        var revision = GovernedLoopRevisionArtifactFactory.Create(1, graph.RevisionReference, null, null, "create-sequential", "user-owner", Now);
        return GovernedLoopGraphRevisionArtifactFactory.Create(1, revision, graph);
    }

    internal static GovernedLoopNodeDefinition Node(string id, GovernedLoopNodeDescriptor descriptor)
        => new(id, descriptor, [], GovernedLoopAuthorityCeiling.Create([]), new Dictionary<string, string>());

    internal static GovernedLoopNodeDefinition Trigger(string id)
        => new(
            id,
            GovernedLoopSequentialNodeDescriptors.ManualTrigger,
            [
                Port("request", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data),
                Port("invocation-context", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Context),
            ],
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string>());

    internal static GovernedLoopNodeDefinition Inference(string id, string instruction = "Answer safely.")
        => new(
            id,
            GovernedLoopSequentialNodeDescriptors.ProviderInference,
            [
                Port("request", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data),
                Port("invocation-context", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Context),
                Port("result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data),
            ],
            GovernedLoopAuthorityCeiling.Create([ModelInferenceCapabilityId]),
            new Dictionary<string, string> { ["instruction"] = instruction });

    internal static GovernedLoopNodeDefinition Exit(string id)
        => new(
            id,
            GovernedLoopSequentialNodeDescriptors.SuccessExit,
            [
                Port("result", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data),
                Port("published-result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data),
            ],
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string>());

    internal static GovernedLoopGraphRevisionArtifact Rebuild(
        GovernedLoopGraphDefinition source,
        IReadOnlyList<GovernedLoopNodeDefinition>? nodes = null,
        IReadOnlyList<GovernedLoopBindingDefinition>? bindings = null,
        IReadOnlyList<GovernedLoopValueSchemaDefinition>? valueSchemas = null,
        GovernedLoopOutputContract? outputContract = null,
        GovernedLoopAuthorityCeiling? authorityCeiling = null)
        => Artifact(
            nodes ?? source.Nodes,
            source.ControlEdges,
            source.TerminalNodeIds,
            source.OwningRole,
            bindings ?? source.Bindings,
            valueSchemas ?? source.ValueSchemas,
            outputContract ?? source.OutputContract,
            authorityCeiling ?? source.AuthorityCeiling);

    internal static GovernedLoopPortDefinition Port(
        string id,
        GovernedLoopPortDirection direction,
        GovernedLoopBindingKind kind,
        string schemaId = "text",
        bool required = true)
        => new(id, direction, kind, schemaId, required);

    internal static string Hash(char value) => new(value, 64);
}
