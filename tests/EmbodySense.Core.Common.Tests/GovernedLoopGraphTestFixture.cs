using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Common.Tests;

internal static class GovernedLoopGraphTestFixture
{
    public const string ModelInferenceCapability = "org.embodysense/model-inference";
    public const string WorkspaceReadCapability = "org.embodysense/workspace-read";

    public static GovernedLoopGraphDefinition Create(
        string graphId = "research-loop",
        string revisionId = "revision-1",
        string purpose = "Research one question within explicit context and authority.",
        ContextualRoleRevisionPin? owningRole = null,
        string entryNodeId = "trigger",
        IEnumerable<string>? terminalNodeIds = null,
        GovernedLoopAuthorityCeiling? authorityCeiling = null,
        IEnumerable<GovernedLoopValueSchemaDefinition>? schemas = null,
        IEnumerable<GovernedLoopNodeDefinition>? nodes = null,
        IEnumerable<GovernedLoopControlEdgeDefinition>? edges = null,
        IEnumerable<GovernedLoopBindingDefinition>? bindings = null,
        GovernedLoopOutputContract? output = null,
        GovernedLoopDisplayMetadata? display = null,
        int schemaVersion = GovernedLoopGraphDefinition.CurrentSchemaVersion)
    {
        authorityCeiling ??= GovernedLoopAuthorityCeiling.Create([ModelInferenceCapability, WorkspaceReadCapability]);
        schemas ??= Schemas();
        nodes ??= Nodes();
        edges ??= Edges();
        bindings ??= Bindings();
        output ??= Output();
        display ??= Display();
        owningRole ??= Role();
        terminalNodeIds ??= ["exit"];
        return GovernedLoopGraphDefinition.Create(schemaVersion, graphId, revisionId, purpose, owningRole, entryNodeId, terminalNodeIds, authorityCeiling, schemas, nodes, edges, bindings, output, display);
    }

    public static ContextualRoleRevisionPin Role(
        string roleId = "researcher",
        int revision = 1,
        char contentHash = 'a')
    {
        return new ContextualRoleRevisionPin(
            new ContextualRoleRevisionIdentity(roleId, revision),
            new string(contentHash, 64));
    }

    public static GovernedLoopValueSchemaDefinition[] Schemas()
    {
        return [new GovernedLoopValueSchemaDefinition("text", GovernedLoopValueKind.Text, false)];
    }

    public static GovernedLoopNodeDefinition[] Nodes()
    {
        return
        [
            new GovernedLoopNodeDefinition(
                "trigger",
                new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Trigger, "manual-trigger", 1),
                [OutputPort("request", GovernedLoopBindingKind.Data), OutputPort("invocation-context", GovernedLoopBindingKind.Context)],
                GovernedLoopAuthorityCeiling.Create([]),
                new Dictionary<string, string>()),
            new GovernedLoopNodeDefinition(
                "infer",
                new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Inference, "provider-inference", 1),
                [InputPort("request", GovernedLoopBindingKind.Data), InputPort("invocation-context", GovernedLoopBindingKind.Context), OutputPort("result", GovernedLoopBindingKind.Data)],
                GovernedLoopAuthorityCeiling.Create([ModelInferenceCapability]),
                new Dictionary<string, string> { ["instruction"] = "Answer using only explicitly bound inputs." }),
            new GovernedLoopNodeDefinition(
                "exit",
                new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Exit, "success-exit", 1),
                [InputPort("result", GovernedLoopBindingKind.Data), OutputPort("published-result", GovernedLoopBindingKind.Data)],
                GovernedLoopAuthorityCeiling.Create([]),
                new Dictionary<string, string>())
        ];
    }

    public static GovernedLoopControlEdgeDefinition[] Edges()
    {
        return
        [
            new GovernedLoopControlEdgeDefinition("trigger-to-infer", "trigger", "infer", GovernedLoopControlCondition.Always),
            new GovernedLoopControlEdgeDefinition("infer-to-exit", "infer", "exit", GovernedLoopControlCondition.Success)
        ];
    }

    public static GovernedLoopBindingDefinition[] Bindings()
    {
        return
        [
            new GovernedLoopBindingDefinition("request-binding", GovernedLoopBindingKind.Data, "trigger", "request", "infer", "request"),
            new GovernedLoopBindingDefinition("context-binding", GovernedLoopBindingKind.Context, "trigger", "invocation-context", "infer", "invocation-context"),
            new GovernedLoopBindingDefinition("result-binding", GovernedLoopBindingKind.Data, "infer", "result", "exit", "result")
        ];
    }

    public static GovernedLoopOutputContract Output()
    {
        return new GovernedLoopOutputContract("Return the bounded research result.", [new GovernedLoopOutputDefinition("result", "text", "exit", "published-result", true)]);
    }

    public static GovernedLoopDisplayMetadata Display(string name = "Research loop", int x = 100, int y = 200)
    {
        return new GovernedLoopDisplayMetadata(
            name,
            "Display-only authoring metadata.",
            [
                new GovernedLoopNodeDisplayMetadata("trigger", "Manual trigger", "Collect input.", x, y),
                new GovernedLoopNodeDisplayMetadata("infer", "Inference", "Answer the question.", x + 100, y),
                new GovernedLoopNodeDisplayMetadata("exit", "Exit", "Publish the result.", x + 200, y)
            ]);
    }

    public static GovernedLoopPortDefinition InputPort(string id, GovernedLoopBindingKind kind, string schemaId = "text", bool required = true)
    {
        return new GovernedLoopPortDefinition(id, GovernedLoopPortDirection.Input, kind, schemaId, required);
    }

    public static GovernedLoopPortDefinition OutputPort(string id, GovernedLoopBindingKind kind, string schemaId = "text", bool required = true)
    {
        return new GovernedLoopPortDefinition(id, GovernedLoopPortDirection.Output, kind, schemaId, required);
    }
}
