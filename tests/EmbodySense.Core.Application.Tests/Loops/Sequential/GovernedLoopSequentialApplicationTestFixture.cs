using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Sequential.Models;
using EmbodySense.Core.Common.Loops.Sequential;
using EmbodySense.Core.Common.Loops.PureNodes;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.LocalWorkspace.Actions;
using EmbodySense.Core.Common.LocalWorkspace.Actions.Models;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Application.Tests.Loops.Sequential;

internal static class GovernedLoopSequentialApplicationTestFixture
{
    internal const string ConversationTurnCapabilityId = "org.embodysense/conversation-turn";
    internal const string ModelInferenceCapabilityId = "org.embodysense/model-inference";
    internal const string ModelProfileCapabilityId = "org.embodysense/model-profile/codex";
    internal const string ScheduleTriggerCapabilityId = "org.embodysense/triggers/time";
    internal const string WorkspaceCommandCapabilityId = "org.embodysense/workspace-command";

    internal static readonly DateTimeOffset Now = new(2026, 8, 10, 22, 0, 0, TimeSpan.Zero);

    internal static GovernedLoopGraphRevisionArtifact LinearArtifact(
        int inferenceCount = 1,
        IReadOnlyList<string>? inferenceIds = null,
        Func<int, GovernedLoopNodeDescriptor>? inferenceDescriptor = null,
        ContextualRoleRevisionPin? owningRole = null,
        bool allowWorkspaceTools = false,
        bool scheduleTrigger = false)
    {
        inferenceIds ??= Enumerable.Range(1, inferenceCount).Select(index => $"infer-{index:D2}").ToArray();
        if (inferenceIds.Count != inferenceCount)
        {
            throw new ArgumentException("Inference identities must match the requested count.", nameof(inferenceIds));
        }

        var nodes = new List<GovernedLoopNodeDefinition>
        {
            Trigger("trigger", scheduleTrigger),
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
                (allowWorkspaceTools, scheduleTrigger) switch
                {
                    (true, true) => [ConversationTurnCapabilityId, ModelInferenceCapabilityId, ModelProfileCapabilityId, ScheduleTriggerCapabilityId, WorkspaceCommandCapabilityId],
                    (true, false) => [ConversationTurnCapabilityId, ModelInferenceCapabilityId, ModelProfileCapabilityId, WorkspaceCommandCapabilityId],
                    (false, true) => [ConversationTurnCapabilityId, ModelInferenceCapabilityId, ModelProfileCapabilityId, ScheduleTriggerCapabilityId],
                    _ => [ConversationTurnCapabilityId, ModelInferenceCapabilityId, ModelProfileCapabilityId],
                }));
    }

    internal static GovernedLoopGraphRevisionArtifact MixedPureArtifact(ContextualRoleRevisionPin? owningRole = null)
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
            owningRole,
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

    internal static GovernedLoopGraphRevisionArtifact WorkspaceActionArtifact(
        WorkspaceActionKind kind = WorkspaceActionKind.Write,
        string? inputJson = null,
        ContextualRoleRevisionPin? owningRole = null)
    {
        inputJson ??= "{\"precondition\":{\"kind\":\"expectedAbsent\"},\"schemaVersion\":1,\"scopeId\":\"workspace\",\"segments\":[{\"kind\":\"literalUtf8\",\"literal\":\"hello\"}],\"target\":\"notes.txt\"}";
        Assert.True(WorkspaceActionInputContract.TryParse(inputJson, kind, out var input, out var reason), reason);
        var nodes = new GovernedLoopNodeDefinition[]
        {
            Trigger("trigger"),
            Inference("infer", "Produce one bounded result before the exact Action."),
            new(
                "workspace-action",
                kind switch
                {
                    WorkspaceActionKind.Append => GovernedLoopSequentialNodeDescriptors.WorkspaceAppend,
                    WorkspaceActionKind.Write => GovernedLoopSequentialNodeDescriptors.WorkspaceWrite,
                    WorkspaceActionKind.Delete => GovernedLoopSequentialNodeDescriptors.WorkspaceDelete,
                    _ => throw new ArgumentOutOfRangeException(nameof(kind)),
                },
                [Port("result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data)],
                GovernedLoopAuthorityCeiling.Create([WorkspaceCommandCapabilityId]),
                new Dictionary<string, string> { ["input"] = WorkspaceActionInputContract.Encode(input!) }),
            Exit("exit"),
        };
        return Artifact(
            nodes,
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-infer", "trigger", "infer", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("infer-to-action", "infer", "workspace-action", GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("action-to-exit", "workspace-action", "exit", GovernedLoopControlCondition.Success),
            ],
            ["exit"],
            owningRole,
            bindings:
            [
                new GovernedLoopBindingDefinition("request-to-infer", GovernedLoopBindingKind.Data, "trigger", "request", "infer", "request"),
                new GovernedLoopBindingDefinition("context-to-infer", GovernedLoopBindingKind.Context, "trigger", "invocation-context", "infer", "invocation-context"),
                new GovernedLoopBindingDefinition("action-result-to-exit", GovernedLoopBindingKind.Data, "workspace-action", "result", "exit", "result"),
            ],
            authorityCeiling: GovernedLoopAuthorityCeiling.Create([ConversationTurnCapabilityId, ModelInferenceCapabilityId, ModelProfileCapabilityId, WorkspaceCommandCapabilityId]));
    }

    internal static GovernedLoopSequentialInvocationSnapshot InvocationSnapshot(
        GovernedLoopGraphRevisionArtifact artifact,
        bool includeConversation = true)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var context = CustomLoopContextSnapshot.CreateEmpty(Now);
        return GovernedLoopSequentialContractHash.Apply(new GovernedLoopSequentialInvocationSnapshot(
            GovernedLoopSequentialInvocationSnapshot.CurrentSchemaVersion,
            "Execute the exact admitted request.",
            new CustomLoopModelSnapshot("provider", "model"),
            includeConversation
                ? new CustomLoopConversationReference("conversation-1", "version-1", Now.AddMinutes(-1))
                : null,
            context.CapturedAtUtc,
            context.SourceManifest,
            string.Empty));
    }

    internal static GovernedLoopGraphRevisionArtifact ParallelAllJoinArtifact(ContextualRoleRevisionPin? owningRole = null)
    {
        var branches = new[] { "branch-a", "branch-b" };
        var nodes = new List<GovernedLoopNodeDefinition>
        {
            Trigger("trigger"),
            Inference("infer"),
        };
        nodes.AddRange(branches.Select(id => new GovernedLoopNodeDefinition(
            id,
            GovernedLoopSequentialNodeDescriptors.IdentityTransform,
            [
                Port(GovernedLoopPureNodeVocabulary.InputPort, GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data),
                Port(GovernedLoopPureNodeVocabulary.OutputPort, GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data),
            ],
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string>())));
        nodes.Add(new GovernedLoopNodeDefinition(
            "join",
            GovernedLoopSequentialNodeDescriptors.AllJoin,
            [],
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string>()));
        nodes.Add(Exit("exit"));

        return Artifact(
            nodes,
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-infer", "trigger", "infer", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("infer-to-branch-a", "infer", "branch-a", GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("infer-to-branch-b", "infer", "branch-b", GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("branch-a-to-join", "branch-a", "join", GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("branch-b-to-join", "branch-b", "join", GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("join-to-exit", "join", "exit", GovernedLoopControlCondition.Success),
            ],
            ["exit"],
            owningRole,
            bindings:
            [
                new GovernedLoopBindingDefinition("request-to-infer", GovernedLoopBindingKind.Data, "trigger", "request", "infer", "request"),
                new GovernedLoopBindingDefinition("context-to-infer", GovernedLoopBindingKind.Context, "trigger", "invocation-context", "infer", "invocation-context"),
                new GovernedLoopBindingDefinition("result-to-branch-a", GovernedLoopBindingKind.Data, "infer", "result", "branch-a", GovernedLoopPureNodeVocabulary.InputPort),
                new GovernedLoopBindingDefinition("result-to-branch-b", GovernedLoopBindingKind.Data, "infer", "result", "branch-b", GovernedLoopPureNodeVocabulary.InputPort),
                new GovernedLoopBindingDefinition("result-to-exit", GovernedLoopBindingKind.Data, "infer", "result", "exit", "result"),
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
        authorityCeiling ??= GovernedLoopAuthorityCeiling.Create([ConversationTurnCapabilityId, ModelInferenceCapabilityId, ModelProfileCapabilityId]);
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
                nodes.Select((node, index) => new GovernedLoopNodeDisplayMetadata(node.Id, node.Id, "Node.", index * 100, 0)).ToArray()),
            GovernedModelProfileApplicationTestFixture.DefaultRoutingPolicy());
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
            GovernedLoopAuthorityCeiling.Create(
                descriptor == GovernedLoopSequentialNodeDescriptors.ProviderInference
                    ? [ModelInferenceCapabilityId, ModelProfileCapabilityId]
                    : []),
            new Dictionary<string, string>());

    internal static GovernedLoopNodeDefinition Trigger(string id, bool scheduled = false)
        => new(
            id,
            scheduled
                ? GovernedLoopSequentialNodeDescriptors.ScheduleTrigger
                : GovernedLoopSequentialNodeDescriptors.ManualTrigger,
            [
                Port("request", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data),
                Port("invocation-context", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Context),
            ],
            GovernedLoopAuthorityCeiling.Create(scheduled ? [ScheduleTriggerCapabilityId] : []),
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
                    ? [ModelInferenceCapabilityId, ModelProfileCapabilityId, WorkspaceCommandCapabilityId]
                    : [ModelInferenceCapabilityId, ModelProfileCapabilityId]),
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
        var capabilityAdmission = CapabilityAdmission(artifact, workspaceId);
        var effectiveAuthority = AuthorityCeilingIntersection.EmptyCeiling();
        var grantProfile = new AuthorityGrantProfilePin(new AuthorityProfileReference(profileId!, profileRevision!), profileHash!);
        var grantBoundary = new AuthorityGrantBoundary(Now.AddHours(-1), Now.AddHours(1), AuthorityGrantCompletionConstraintKind.None);
        var inferenceNodes = artifact.Graph.Nodes.Where(node => node.Descriptor.Kind == GovernedLoopNodeKind.Inference).Select(node => node.Id).ToArray();
        var evidence = inferenceNodes.Length == 0
            ? GovernedModelProfileApplicationTestFixture.EmptyRoutingEvidence(intent, execution, grantProfile, grantBoundary, Hash('9'), effectiveAuthority, capabilityAdmission, Now)
            : GovernedModelProfileApplicationTestFixture.RoutingEvidenceForInference(
                intent,
                execution,
                grantProfile,
                grantBoundary,
                Hash('9'),
                effectiveAuthority,
                capabilityAdmission,
                Now,
                nodeId: inferenceNodes[0],
                nodeIds: inferenceNodes);
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

    private static CapabilityAdmissionSnapshot CapabilityAdmission(GovernedLoopGraphRevisionArtifact artifact, string workspaceId)
    {
        Assert.True(CapabilityId.TryParse("org.embodysense/loop-" + artifact.ArtifactHash[..32], out var subject, out _));
        Assert.True(CapabilityVersionRange.TryParse("*", out var versions, out _));
        Assert.True(CapabilityIntegrityDigest.TryParse("sha256:" + artifact.ArtifactHash, out var checksum, out _));
        var required = artifact.Graph.AuthorityCeiling.CapabilityIds.Select(value =>
        {
            Assert.True(CapabilityId.TryParse(value, out var id, out _));
            return new CapabilityDependency(id!, versions!);
        }).ToArray();
        var manifest = new CapabilityDependencyManifest(
            CapabilityDependencyManifest.CurrentSchemaVersion,
            CapabilityDependencyManifestKind.LoopPackage,
            subject!,
            required,
            [],
            new CapabilityDependencyArtifactMetadata(checksum, null));
        return TestCapabilityAdmissionFactory.Create(manifest, Now) with { WorkspaceScopeId = workspaceId };
    }
}
