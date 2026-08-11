using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.PureNodes;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Sequential;

internal static class GovernedLoopPureSchemaAdmissionTestFixture
{
    internal static GovernedLoopGraphRevisionArtifact SchemaConformanceArtifact(
        bool formatRoot = false,
        bool formatElement = false,
        bool cycle = false)
    {
        var inputSchema = cycle || formatElement
            ? new GovernedLoopValueSchemaDefinition(
                "input",
                GovernedLoopValueKind.Array,
                false,
                Format: formatRoot ? "formatted-array" : null,
                ElementSchemaId: cycle ? "input" : "element")
            : new GovernedLoopValueSchemaDefinition(
                "input",
                GovernedLoopValueKind.Text,
                false,
                Format: formatRoot ? "formatted-text" : null);
        var schemas = new List<GovernedLoopValueSchemaDefinition>
        {
            new("boolean", GovernedLoopValueKind.Boolean, false),
            inputSchema,
            new("text", GovernedLoopValueKind.Text, false),
        };
        if (formatElement)
        {
            schemas.Add(new GovernedLoopValueSchemaDefinition("element", GovernedLoopValueKind.Text, false, Format: "formatted-element"));
        }

        var trigger = GovernedLoopSequentialApplicationTestFixture.Trigger("trigger") with
        {
            Ports =
            [
                GovernedLoopSequentialApplicationTestFixture.Port("request", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "input"),
                GovernedLoopSequentialApplicationTestFixture.Port("invocation-context", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Context),
            ],
        };
        var schemaCheck = new GovernedLoopNodeDefinition(
            "schema-check",
            GovernedLoopSequentialNodeDescriptors.SchemaConformance,
            [
                GovernedLoopSequentialApplicationTestFixture.Port(GovernedLoopPureNodeVocabulary.InputPort, GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data, "input"),
                GovernedLoopSequentialApplicationTestFixture.Port(GovernedLoopPureNodeVocabulary.ResultPort, GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "boolean"),
            ],
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string>());
        var inference = GovernedLoopSequentialApplicationTestFixture.Inference("infer") with
        {
            Ports = GovernedLoopSequentialApplicationTestFixture.Inference("infer").Ports.Select(port => string.Equals(port.Id, "request", StringComparison.Ordinal)
                ? port with { ValueSchemaId = "input" }
                : port).ToArray(),
        };
        return GovernedLoopSequentialApplicationTestFixture.Artifact(
            [trigger, schemaCheck, inference, GovernedLoopSequentialApplicationTestFixture.Exit("exit")],
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-schema", "trigger", "schema-check", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("schema-to-infer", "schema-check", "infer", GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("infer-to-exit", "infer", "exit", GovernedLoopControlCondition.Success),
            ],
            ["exit"],
            bindings:
            [
                new GovernedLoopBindingDefinition("request-to-schema", GovernedLoopBindingKind.Data, "trigger", "request", "schema-check", GovernedLoopPureNodeVocabulary.InputPort),
                new GovernedLoopBindingDefinition("request-to-infer", GovernedLoopBindingKind.Data, "trigger", "request", "infer", "request"),
                new GovernedLoopBindingDefinition("context-to-infer", GovernedLoopBindingKind.Context, "trigger", "invocation-context", "infer", "invocation-context"),
                new GovernedLoopBindingDefinition("result-to-exit", GovernedLoopBindingKind.Data, "infer", "result", "exit", "result"),
            ],
            valueSchemas: schemas);
    }

    internal static GovernedLoopGraphRevisionArtifact ConcatArtifact(
        bool formatArray = false,
        bool formatElement = false)
    {
        var trigger = GovernedLoopSequentialApplicationTestFixture.Trigger("trigger") with
        {
            Ports =
            [
                GovernedLoopSequentialApplicationTestFixture.Port("request", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "items"),
                GovernedLoopSequentialApplicationTestFixture.Port("invocation-context", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Context),
            ],
        };
        var concat = new GovernedLoopNodeDefinition(
            "concat",
            GovernedLoopSequentialNodeDescriptors.OrderedTextConcat,
            [
                GovernedLoopSequentialApplicationTestFixture.Port(GovernedLoopPureNodeVocabulary.ValuesPort, GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data, "items"),
                GovernedLoopSequentialApplicationTestFixture.Port(GovernedLoopPureNodeVocabulary.OutputPort, GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data),
            ],
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string> { [GovernedLoopPureNodeVocabulary.SeparatorParameter] = "," });
        return GovernedLoopSequentialApplicationTestFixture.Artifact(
            [trigger, concat, GovernedLoopSequentialApplicationTestFixture.Inference("infer"), GovernedLoopSequentialApplicationTestFixture.Exit("exit")],
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-concat", "trigger", "concat", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("concat-to-infer", "concat", "infer", GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("infer-to-exit", "infer", "exit", GovernedLoopControlCondition.Success),
            ],
            ["exit"],
            bindings:
            [
                new GovernedLoopBindingDefinition("request-to-values", GovernedLoopBindingKind.Data, "trigger", "request", "concat", GovernedLoopPureNodeVocabulary.ValuesPort),
                new GovernedLoopBindingDefinition("concat-to-infer", GovernedLoopBindingKind.Data, "concat", GovernedLoopPureNodeVocabulary.OutputPort, "infer", "request"),
                new GovernedLoopBindingDefinition("context-to-infer", GovernedLoopBindingKind.Context, "trigger", "invocation-context", "infer", "invocation-context"),
                new GovernedLoopBindingDefinition("result-to-exit", GovernedLoopBindingKind.Data, "infer", "result", "exit", "result"),
            ],
            valueSchemas:
            [
                new GovernedLoopValueSchemaDefinition("element", GovernedLoopValueKind.Text, false, Format: formatElement ? "formatted-element" : null),
                new GovernedLoopValueSchemaDefinition("items", GovernedLoopValueKind.Array, false, Format: formatArray ? "formatted-array" : null, ElementSchemaId: "element"),
                new GovernedLoopValueSchemaDefinition("text", GovernedLoopValueKind.Text, false),
            ]);
    }
}
