using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.PureNodes;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Application.Tests.Loops.Sequential;

internal static class GovernedLoopSequentialApplicationTestFixture
{
    internal const string ConversationTurnCapabilityId = "org.embodysense/conversation-turn";
    internal const string ModelInferenceCapabilityId = "org.embodysense/model-inference";
    internal const string WorkspaceCommandCapabilityId = "org.embodysense/workspace-command";

    internal static readonly DateTimeOffset Now = new(2026, 8, 10, 22, 0, 0, TimeSpan.Zero);

    internal static GovernedLoopGraphRevisionArtifact LinearArtifact(
        int inferenceCount = 1,
        IReadOnlyList<string>? inferenceIds = null,
        Func<int, GovernedLoopNodeDescriptor>? inferenceDescriptor = null,
        ContextualRoleRevisionPin? owningRole = null,
        bool allowWorkspaceTools = false)
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
            ? Inference(id, $"Execute bounded inference step {index + 1}.", allowWorkspaceTools)
            : Inference(id, $"Execute bounded inference step {index + 1}.", allowWorkspaceTools) with { Descriptor = inferenceDescriptor(index) }));
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
        return Artifact(
            nodes,
            edges,
            ["exit"],
            owningRole,
            bindings,
            authorityCeiling: GovernedLoopAuthorityCeiling.Create(
                allowWorkspaceTools
                    ? [ConversationTurnCapabilityId, ModelInferenceCapabilityId, WorkspaceCommandCapabilityId]
                    : [ConversationTurnCapabilityId, ModelInferenceCapabilityId]));
    }

    internal static GovernedLoopGraphRevisionArtifact MixedPureArtifact()
    {
        var nodes = new GovernedLoopNodeDefinition[]
        {
            Trigger("trigger"),
            new(
                "identity",
                GovernedLoopSequentialNodeDescriptors.IdentityTransform,
                [Port(GovernedLoopPureNodeVocabulary.InputPort, GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data), Port(GovernedLoopPureNodeVocabulary.OutputPort, GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data)],
                GovernedLoopAuthorityCeiling.Create([]),
                new Dictionary<string, string>()),
            Inference("infer", "Answer from the exact transformed request."),
            new(
                "validate-length",
                GovernedLoopSequentialNodeDescriptors.TextLength,
                [Port(GovernedLoopPureNodeVocabulary.InputPort, GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data), Port(GovernedLoopPureNodeVocabulary.ResultPort, GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "boolean")],
                GovernedLoopAuthorityCeiling.Create([]),
                new Dictionary<string, string>
                {
                    [GovernedLoopPureNodeVocabulary.MinimumParameter] = "1",
                    [GovernedLoopPureNodeVocabulary.MaximumParameter] = CustomLoopLimits.MaxGraphTypedValueStringCharacters.ToString(System.Globalization.CultureInfo.InvariantCulture)
                }),
            Exit("exit")
        };
        return Artifact(
            nodes,
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-identity", "trigger", "identity", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("identity-to-infer", "identity", "infer", GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("infer-to-validation", "infer", "validate-length", GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("validation-to-exit", "validate-length", "exit", GovernedLoopControlCondition.Success)
            ],
            ["exit"],
            bindings:
            [
                new GovernedLoopBindingDefinition("request-to-identity", GovernedLoopBindingKind.Data, "trigger", "request", "identity", GovernedLoopPureNodeVocabulary.InputPort),
                new GovernedLoopBindingDefinition("identity-to-request", GovernedLoopBindingKind.Data, "identity", GovernedLoopPureNodeVocabulary.OutputPort, "infer", "request"),
                new GovernedLoopBindingDefinition("context-to-infer", GovernedLoopBindingKind.Context, "trigger", "invocation-context", "infer", "invocation-context"),
                new GovernedLoopBindingDefinition("result-to-validation", GovernedLoopBindingKind.Data, "infer", "result", "validate-length", GovernedLoopPureNodeVocabulary.InputPort),
                new GovernedLoopBindingDefinition("result-to-exit", GovernedLoopBindingKind.Data, "infer", "result", "exit", "result")
            ],
            valueSchemas:
            [
                new GovernedLoopValueSchemaDefinition("boolean", GovernedLoopValueKind.Boolean, false),
                new GovernedLoopValueSchemaDefinition("text", GovernedLoopValueKind.Text, false)
            ]);
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
        authorityCeiling ??= GovernedLoopAuthorityCeiling.Create([ConversationTurnCapabilityId, ModelInferenceCapabilityId]);
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
        => new(
            id,
            descriptor,
            descriptor == GovernedLoopSequentialNodeDescriptors.SuccessExit
                ? [Port("published-result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data)]
                : [],
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string>());

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

    internal static GovernedLoopNodeDefinition Inference(string id, string instruction = "Answer safely.", bool allowWorkspaceTools = false)
        => new(
            id,
            GovernedLoopSequentialNodeDescriptors.ProviderInference,
            [
                Port("request", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data),
                Port("invocation-context", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Context),
                Port("result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data),
            ],
            GovernedLoopAuthorityCeiling.Create(
                allowWorkspaceTools
                    ? [ModelInferenceCapabilityId, WorkspaceCommandCapabilityId]
                    : [ModelInferenceCapabilityId]),
            new Dictionary<string, string> { ["instruction"] = instruction });

    internal static GovernedLoopNodeDefinition Exit(string id)
        => new(
            id,
            GovernedLoopSequentialNodeDescriptors.SuccessExit,
            [
                Port("result", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data),
                Port("published-result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data),
            ],
            GovernedLoopAuthorityCeiling.Create([ConversationTurnCapabilityId]),
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

    internal static GovernedLoopAdmissionReceipt AdmissionReceipt(
        GovernedLoopGraphRevisionArtifact artifact,
        GovernedLoopExecutionBinding execution,
        string workspaceId,
        string operationId,
        string requestHash,
        string graphArtifactHash,
        string graphLayoutHash)
    {
        Assert.True(AuthorityGrantId.TryParse("grant-sequential", out var grantId, out _));
        Assert.True(AuthorityGrantRevision.TryParse("1", out var grantRevision, out _));
        Assert.True(AuthorityProfileId.TryParse("profile-sequential", out var profileId, out _));
        Assert.True(AuthorityProfileRevision.TryParse("1", out var profileRevision, out _));
        Assert.True(AuthorityProfileHash.TryParse("sha256:" + Hash('b'), out var profileHash, out _));
        Assert.True(AuthorityActorId.TryParse("user-owner", out var actorId, out _));
        var publication = GovernedLoopRevisionPublicationPinFactory.Create(1, execution.Revision, "publish-sequential", Hash('7'));
        var intent = new GovernedLoopAdmissionIntent(
            GovernedLoopAdmissionIntent.CurrentSchemaVersion,
            workspaceId,
            operationId,
            requestHash,
            publication,
            new AuthorityGrantReference(grantId!, grantRevision!, "sha256:" + Hash('a')),
            artifact.Graph.OwningRole,
            actorId!,
            "test",
            graphArtifactHash,
            graphLayoutHash);
        var capabilityAdmission = TestCapabilityAdmissionFactory.Create(LoopCapabilityRequirements.CreateDefaultConversationManifest(), Now) with { WorkspaceScopeId = workspaceId };
        var effectiveAuthority = AuthorityCeilingIntersection.EmptyCeiling();
        var evidence = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionEvidence(
            GovernedLoopAdmissionEvidence.CurrentSchemaVersion,
            GovernedLoopAdmissionContractHash.ComputeIntentHash(intent),
            execution,
            new AuthorityGrantProfilePin(new AuthorityProfileReference(profileId!, profileRevision!), profileHash!),
            new AuthorityGrantBoundary(Now.AddHours(-1), Now.AddHours(1), AuthorityGrantCompletionConstraintKind.None),
            Hash('9'),
            effectiveAuthority,
            capabilityAdmission,
            GovernedLoopAdmissionContractHash.CreateEvidenceReferences(intent, effectiveAuthority, capabilityAdmission),
            Now,
            string.Empty));
        return GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionReceipt(
            GovernedLoopAdmissionReceipt.CurrentSchemaVersion,
            intent,
            evidence,
            Now,
            string.Empty));
    }

    internal static GovernedLoopPortDefinition Port(
        string id,
        GovernedLoopPortDirection direction,
        GovernedLoopBindingKind kind,
        string schemaId = "text",
        bool required = true)
        => new(id, direction, kind, schemaId, required);

    internal static string Hash(char value) => new(value, 64);
}
