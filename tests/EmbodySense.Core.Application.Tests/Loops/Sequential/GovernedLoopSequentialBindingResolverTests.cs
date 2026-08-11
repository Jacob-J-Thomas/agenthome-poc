using System.Text.Json;
using EmbodySense.Core.Application.Loops.Admission;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.PureNodes;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Sequential.Models;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Application.Tests.Loops.Sequential;

public sealed class GovernedLoopSequentialBindingResolverTests
{
    [Fact]
    public void Resolve_rejects_a_missing_public_context_without_throwing()
    {
        var result = GovernedLoopSequentialBindingResolver.Resolve(null, null, null, null);

        Assert.False(result.IsResolved);
        Assert.Empty(result.Inputs);
        Assert.Equal("pure-node.context-invalid", result.FailureCode);
        Assert.Equal("$", result.FailurePath);
    }

    [Fact]
    public async Task Resolve_materializes_typed_transform_and_validate_inputs_and_preserves_false_boolean_outputs()
    {
        var context = await ContextAsync();
        var identity = Node(context, "identity");

        var identityResolution = GovernedLoopSequentialBindingResolver.Resolve(context.Artifact, context.Plan, identity, context.Run);

        Assert.True(identityResolution.IsResolved);
        var identityInput = Assert.Single(identityResolution.Inputs);
        Assert.Equal("request-to-identity", identityInput.BindingId);
        Assert.Equal(GovernedLoopValueKind.Text, identityInput.Value.Kind);
        Assert.Equal(context.Invocation.TriggerPrompt, JsonSerializer.Deserialize<string>(identityInput.Value.CanonicalValueJson));

        var afterIdentity = CompletePureNode(context, context.Run, identity, identityResolution.Inputs);
        var afterInference = CompleteInference(context, afterIdentity, Node(context, "infer"), "governed answer");
        var validation = Node(context, "validate-length");

        var validationResolution = GovernedLoopSequentialBindingResolver.Resolve(context.Artifact, context.Plan, validation, afterInference);

        Assert.True(validationResolution.IsResolved);
        var validationInput = Assert.Single(validationResolution.Inputs);
        Assert.Equal("result-to-validation", validationInput.BindingId);
        Assert.Equal(GovernedLoopValueKind.Text, validationInput.Value.Kind);
        Assert.Equal("governed answer", JsonSerializer.Deserialize<string>(validationInput.Value.CanonicalValueJson));

        var afterValidation = CompletePureNode(context, afterInference, validation, validationResolution.Inputs);
        var equalityResolution = GovernedLoopSequentialBindingResolver.Resolve(
            context.Artifact,
            context.Plan,
            Node(context, "equal-false"),
            afterValidation);

        Assert.True(equalityResolution.IsResolved);
        Assert.Equal(["validation-to-equality-left", "validation-to-equality-right"], equalityResolution.Inputs.Select(input => input.BindingId));
        Assert.All(equalityResolution.Inputs, input =>
        {
            Assert.Equal("boolean", input.ValueSchemaId);
            Assert.Equal(GovernedLoopValueKind.Boolean, input.Value.Kind);
            Assert.Equal("false", input.Value.CanonicalValueJson);
        });
    }

    [Fact]
    public async Task Resolve_rejects_a_future_validate_node_when_its_durable_source_evidence_is_missing()
    {
        var context = await ContextAsync();

        var result = GovernedLoopSequentialBindingResolver.Resolve(
            context.Artifact,
            context.Plan,
            Node(context, "validate-length"),
            context.Run);

        Assert.False(result.IsResolved);
        Assert.Empty(result.Inputs);
        Assert.Equal("canonical-binding.activation-invalid", result.FailureCode);
        Assert.Equal("$.frontier", result.FailurePath);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("schema-type-mismatch")]
    public async Task Resolve_rejects_tampered_pure_source_evidence_even_when_the_outer_durable_hashes_are_exact(string substitution)
    {
        var prepared = await PrepareEqualityAsync();
        var completion = prepared.Run.Events.Single(item => item.StepId == "validate-length" && item.Kind == CustomLoopRunEventKind.NodeAttemptCompleted);
        var substitutedJson = substitution switch
        {
            "malformed" => "{",
            "schema-type-mismatch" => completion.PureNodeOutcomeJson!.Replace("\"kind\":\"boolean\"", "\"kind\":\"text\"", StringComparison.Ordinal),
            _ => throw new InvalidOperationException("Unknown source-evidence substitution."),
        };
        Assert.NotEqual(completion.PureNodeOutcomeJson, substitutedJson);
        var tampered = ReplaceCompletedEvidence(
            prepared.Run,
            "validate-length",
            item => item with { PureNodeOutcomeJson = substitutedJson });
        var runValidation = CustomLoopRunValidator.ValidateForDispatch(tampered);
        Assert.True(runValidation.IsValid, string.Join(Environment.NewLine, runValidation.Errors));

        var result = GovernedLoopSequentialBindingResolver.Resolve(
            prepared.Context.Artifact,
            prepared.Context.Plan,
            Node(prepared.Context, "equal-false"),
            tampered);

        Assert.False(result.IsResolved);
        Assert.Empty(result.Inputs);
        Assert.Equal("canonical-binding.source-evidence-invalid", result.FailureCode);
        Assert.Equal("$.bindings[validation-to-equality-left]", result.FailurePath);
    }

    [Fact]
    public async Task Resolve_accepts_the_durable_model_output_bound_and_rejects_one_character_beyond_it()
    {
        var context = await ContextAsync();
        var identity = Node(context, "identity");
        var identityResolution = GovernedLoopSequentialBindingResolver.Resolve(context.Artifact, context.Plan, identity, context.Run);
        var afterIdentity = CompletePureNode(context, context.Run, identity, identityResolution.Inputs);
        var boundedOutput = new string('x', CustomLoopLimits.MaxCanonicalModelOutputCharacters);
        var bounded = CompleteInference(context, afterIdentity, Node(context, "infer"), boundedOutput);

        var accepted = GovernedLoopSequentialBindingResolver.Resolve(
            context.Artifact,
            context.Plan,
            Node(context, "validate-length"),
            bounded);

        Assert.True(accepted.IsResolved);
        Assert.Equal(CustomLoopLimits.MaxCanonicalModelOutputCharacters, JsonSerializer.Deserialize<string>(Assert.Single(accepted.Inputs).Value.CanonicalValueJson)!.Length);

        var overBound = ReplaceCompletedEvidence(
            bounded,
            "infer",
            item => item with
            {
                CanonicalOutput = boundedOutput + "x",
                OriginalOutputCharacterCount = boundedOutput.Length + 1,
            });
        Assert.False(CustomLoopRunValidator.ValidateForDispatch(overBound).IsValid);

        var rejected = GovernedLoopSequentialBindingResolver.Resolve(
            context.Artifact,
            context.Plan,
            Node(context, "validate-length"),
            overBound);

        Assert.False(rejected.IsResolved);
        Assert.Empty(rejected.Inputs);
        Assert.Equal("canonical-binding.context-invalid", rejected.FailureCode);
        Assert.Equal("$", rejected.FailurePath);
    }

    private static async Task<PreparedEquality> PrepareEqualityAsync()
    {
        var context = await ContextAsync();
        var identity = Node(context, "identity");
        var identityResolution = GovernedLoopSequentialBindingResolver.Resolve(context.Artifact, context.Plan, identity, context.Run);
        var afterIdentity = CompletePureNode(context, context.Run, identity, identityResolution.Inputs);
        var afterInference = CompleteInference(context, afterIdentity, Node(context, "infer"), "governed answer");
        var validation = Node(context, "validate-length");
        var validationResolution = GovernedLoopSequentialBindingResolver.Resolve(context.Artifact, context.Plan, validation, afterInference);
        var afterValidation = CompletePureNode(context, afterInference, validation, validationResolution.Inputs);
        return new PreparedEquality(context, afterValidation);
    }

    private static async Task<ResolverContext> ContextAsync()
    {
        var seed = await GovernedLoopSequentialRunMaterializerTests.ContextAsync(includeConversation: false);
        var artifact = FalseValidationArtifact(seed.Receipt.Intent.Role);
        var planResult = GovernedLoopSequentialPlanBuilder.Build(artifact);
        var plan = Assert.IsType<GovernedLoopSequentialPlan>(planResult.Plan);
        var publication = seed.AdmissionRequest.Publication with { Revision = artifact.RevisionArtifact.Revision };
        var admissionRequest = GovernedLoopAdmissionRequestHash.Apply(seed.AdmissionRequest with
        {
            Publication = publication,
            RequestHash = string.Empty,
        });
        var intent = seed.Receipt.Intent with
        {
            RequestHash = admissionRequest.RequestHash,
            Publication = publication,
            GraphArtifactHash = artifact.ArtifactHash,
            GraphLayoutHash = artifact.LayoutHash,
        };
        var execution = GovernedLoopExecutionBinding.Create(
            seed.Receipt.Evidence.Binding.SchemaVersion,
            seed.Receipt.Evidence.Binding.RunId,
            artifact.RevisionArtifact.Revision,
            seed.Receipt.Evidence.Binding.ExecutionGeneration);
        var capabilityAdmission = CapabilityAdmission(artifact, intent.WorkspaceId);
        var evidence = GovernedLoopAdmissionContractHash.Apply(new EmbodySense.Core.Common.Loops.Admission.Models.GovernedLoopAdmissionEvidence(
            seed.Receipt.Evidence.SchemaVersion,
            GovernedLoopAdmissionContractHash.ComputeIntentHash(intent),
            execution,
            seed.Receipt.Evidence.GrantProfile,
            seed.Receipt.Evidence.GrantBoundary,
            seed.Receipt.Evidence.GrantDependencyEvidenceHash,
            seed.Receipt.Evidence.EffectiveAuthority,
            capabilityAdmission,
            GovernedLoopAdmissionContractHash.CreateEvidenceReferences(intent, seed.Receipt.Evidence.EffectiveAuthority, capabilityAdmission),
            seed.Receipt.Evidence.EvaluatedAtUtc,
            string.Empty));
        var receipt = GovernedLoopAdmissionContractHash.Apply(seed.Receipt with
        {
            Intent = intent,
            Evidence = evidence,
            ContentHash = string.Empty,
        });
        var adapterBinding = GovernedLoopSequentialContractHash.Apply(new GovernedLoopSequentialAdapterBinding(
            seed.AdapterBinding.SchemaVersion,
            seed.AdapterBinding.WorkspaceId,
            execution,
            seed.AdapterBinding.AdmissionOperationId,
            receipt,
            receipt.ContentHash,
            admissionRequest.RequestHash,
            seed.AdapterBinding.InvocationPayloadHash,
            artifact.ArtifactHash,
            artifact.LayoutHash,
            string.Empty));
        var request = new GovernedLoopSequentialMaterializationRequest(
            GovernedLoopSequentialMaterializationRequest.CurrentSchemaVersion,
            admissionRequest,
            receipt,
            artifact,
            plan,
            seed.Invocation,
            adapterBinding);
        var store = new GovernedLoopSequentialRunMaterializerTests.RecordingRunStore();
        var materializer = new GovernedLoopSequentialRunMaterializer(
            store,
            new GovernedLoopSequentialRunMaterializerTests.RecordingAuditRecorder(),
            new GovernedLoopSequentialRunMaterializerTests.RecordingEventIdentityGenerator(),
            new GovernedLoopSequentialRunMaterializerTests.FixedTimeProvider(receipt.RecordedAtUtc.AddMinutes(1)));

        var materialized = await materializer.MaterializeAsync(request);

        Assert.True(materialized.IsReady(), materialized.Detail);
        var run = Assert.IsType<CustomLoopRunRecord>(materialized.Run);
        var validation = CustomLoopRunValidator.ValidateForDispatch(run);
        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));
        return new ResolverContext(artifact, plan, seed.Invocation, adapterBinding, run);
    }

    private static GovernedLoopGraphRevisionArtifact FalseValidationArtifact(EmbodySense.Core.Common.ContextualRoles.Models.ContextualRoleRevisionPin owningRole)
    {
        var source = GovernedLoopSequentialApplicationTestFixture.MixedPureArtifact(owningRole).Graph;
        var validation = source.Nodes.Single(node => node.Id == "validate-length") with
        {
            Parameters = new Dictionary<string, string>
            {
                [GovernedLoopPureNodeVocabulary.MinimumParameter] = "0",
                [GovernedLoopPureNodeVocabulary.MaximumParameter] = "0",
            },
        };
        var equality = new GovernedLoopNodeDefinition(
            "equal-false",
            GovernedLoopSequentialNodeDescriptors.CanonicalEquality,
            [
                GovernedLoopSequentialApplicationTestFixture.Port(GovernedLoopPureNodeVocabulary.LeftPort, GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data, "boolean"),
                GovernedLoopSequentialApplicationTestFixture.Port(GovernedLoopPureNodeVocabulary.RightPort, GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data, "boolean"),
                GovernedLoopSequentialApplicationTestFixture.Port(GovernedLoopPureNodeVocabulary.ResultPort, GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "boolean"),
            ],
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string>());
        return GovernedLoopSequentialApplicationTestFixture.Artifact(
            [.. source.Nodes.Select(node => node.Id == validation.Id ? validation : node), equality],
            [
                .. source.ControlEdges.Where(edge => edge.Id != "validation-to-exit"),
                new GovernedLoopControlEdgeDefinition("validation-to-equality", "validate-length", equality.Id, GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("equality-to-exit", equality.Id, "exit", GovernedLoopControlCondition.Success),
            ],
            source.TerminalNodeIds,
            owningRole,
            [
                .. source.Bindings,
                new GovernedLoopBindingDefinition("validation-to-equality-left", GovernedLoopBindingKind.Data, "validate-length", GovernedLoopPureNodeVocabulary.ResultPort, equality.Id, GovernedLoopPureNodeVocabulary.LeftPort),
                new GovernedLoopBindingDefinition("validation-to-equality-right", GovernedLoopBindingKind.Data, "validate-length", GovernedLoopPureNodeVocabulary.ResultPort, equality.Id, GovernedLoopPureNodeVocabulary.RightPort),
            ],
            source.ValueSchemas,
            source.OutputContract,
            source.AuthorityCeiling);
    }

    private static CapabilityAdmissionSnapshot CapabilityAdmission(GovernedLoopGraphRevisionArtifact artifact, string workspaceId)
    {
        Assert.True(CapabilityId.TryParse("org.embodysense/loop-" + artifact.ArtifactHash[..32], out var subject, out _));
        Assert.True(CapabilityVersionRange.TryParse("*", out var any, out _));
        Assert.True(CapabilityIntegrityDigest.TryParse("sha256:" + artifact.ArtifactHash, out var checksum, out _));
        var dependencies = artifact.Graph.AuthorityCeiling.CapabilityIds
            .Order(StringComparer.Ordinal)
            .Select(value =>
            {
                Assert.True(CapabilityId.TryParse(value, out var id, out _));
                return new CapabilityDependency(id!, any!);
            })
            .ToArray();
        var manifest = new CapabilityDependencyManifest(
            CapabilityDependencyManifest.CurrentSchemaVersion,
            CapabilityDependencyManifestKind.LoopPackage,
            subject!,
            dependencies,
            [],
            new CapabilityDependencyArtifactMetadata(checksum, null));
        return TestCapabilityAdmissionFactory.Create(manifest, GovernedLoopSequentialApplicationTestFixture.Now) with
        {
            WorkspaceScopeId = workspaceId,
        };
    }

    private static GovernedLoopSequentialPlanNode Node(ResolverContext context, string nodeId)
        => context.Plan.Nodes.Single(node => string.Equals(node.NodeId, nodeId, StringComparison.Ordinal));

    private static CustomLoopRunRecord CompletePureNode(
        ResolverContext context,
        CustomLoopRunRecord run,
        GovernedLoopSequentialPlanNode node,
        IReadOnlyList<GovernedLoopTypedBindingValue> inputs)
    {
        Assert.True(GovernedLoopPureNodeEvaluator.TryEvaluate(
            context.Artifact.Graph,
            node.NodeId,
            inputs,
            out var output,
            out var validationEvidence,
            out var evaluation), string.Join(Environment.NewLine, evaluation.Errors));
        Assert.True(GovernedLoopPureNodeOutcome.TryCreate(
            context.Artifact.Graph,
            node.NodeId,
            inputs,
            [output!],
            validationEvidence,
            out var outcome,
            out var outcomeValidation), string.Join(Environment.NewLine, outcomeValidation.Errors));
        return CompleteNode(context, run, node, pureNodeOutcomeJson: outcome!.CanonicalJson, canonicalOutput: null);
    }

    private static CustomLoopRunRecord CompleteInference(
        ResolverContext context,
        CustomLoopRunRecord run,
        GovernedLoopSequentialPlanNode node,
        string output)
        => CompleteNode(context, run, node, pureNodeOutcomeJson: null, canonicalOutput: output);

    private static CustomLoopRunRecord CompleteNode(
        ResolverContext context,
        CustomLoopRunRecord run,
        GovernedLoopSequentialPlanNode node,
        string? pureNodeOutcomeJson,
        string? canonicalOutput)
    {
        var startedAtUtc = run.UpdatedAtUtc.AddMilliseconds(1);
        var completedAtUtc = startedAtUtc.AddMilliseconds(1);
        var attemptOperationId = $"start-{node.NodeId}";
        var readyActivation = Assert.IsType<GovernedLoopNodeExecutionEvidence>(
            GovernedLoopSequentialFrontierMachine.Select(run.Frontier, context.AdapterBinding, context.Plan).Activation);
        var start = WithSequentialEvidence(
            new CustomLoopRunEvent(
                run.Events.Length + 1,
                attemptOperationId,
                startedAtUtc,
                CustomLoopRunEventKind.NodeAttemptStarted,
                1,
                node.NodeId,
                1,
                "The exact canonical node attempt started.",
                [],
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                GovernedLoopSequentialNodeDescriptors.IsPure(node.Descriptor)
                    ? CustomLoopLimits.MaxGraphPureNodeOutcomeEvidenceReservationUtf8Bytes
                    : CustomLoopLimits.MaxAttemptEvidenceReservationUtf8Bytes),
            context.AdapterBinding,
            node,
            readyActivation,
            CustomLoopSequentialNodeEvidenceKind.DispatchStarted,
            CustomLoopSequentialNodeDisposition.Unknown);
        var running = Frontier(GovernedLoopSequentialFrontierMachine.Start(
            run.Frontier,
            context.AdapterBinding,
            context.Plan,
            node,
            readyActivation,
            1,
            attemptOperationId,
            startedAtUtc));
        var completed = WithSequentialEvidence(
            new CustomLoopRunEvent(
                run.Events.Length + 2,
                $"complete-{node.NodeId}",
                completedAtUtc,
                CustomLoopRunEventKind.NodeAttemptCompleted,
                1,
                node.NodeId,
                1,
                "The exact canonical node attempt completed.",
                [],
                canonicalOutput,
                canonicalOutput?.Length,
                canonicalOutput is null ? null : false,
                canonicalOutput is null ? null : false,
                canonicalOutput is null ? null : false,
                null,
                canonicalOutput is null ? null : run.ModelSnapshot.Provider,
                canonicalOutput is null ? null : run.ModelSnapshot.Model,
                canonicalOutput is null ? null : $"response-{node.NodeId}",
                null)
            {
                PureNodeOutcomeJson = pureNodeOutcomeJson,
            },
            context.AdapterBinding,
            node,
            Assert.IsType<GovernedLoopNodeExecutionEvidence>(
                GovernedLoopSequentialFrontierMachine.Select(running, context.AdapterBinding, context.Plan).Activation),
            CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
            CustomLoopSequentialNodeDisposition.Completed);
        var runningActivation = Assert.IsType<GovernedLoopNodeExecutionEvidence>(
            GovernedLoopSequentialFrontierMachine.Select(running, context.AdapterBinding, context.Plan).Activation);
        var advanced = Frontier(GovernedLoopSequentialFrontierMachine.CompleteRunning(
            running,
            context.AdapterBinding,
            context.Plan,
            node,
            runningActivation,
            1,
            attemptOperationId,
            completed.EventId,
            completed.SequentialNodeEvidence!.OutcomeArtifactHash,
            GovernedLoopControlCondition.Success,
            [],
            completedAtUtc));
        var successor = run with
        {
            LifecycleVersion = run.LifecycleVersion + 1,
            Status = CustomLoopRunStatus.Running,
            UpdatedAtUtc = completedAtUtc,
            ExecutionClock = new CustomLoopExecutionClock(run.ExecutionClock.AccumulatedRunningMilliseconds, run.ExecutionClock.ActiveSinceUtc ?? run.CreatedAtUtc),
            Events = [.. run.Events, start, completed],
            Frontier = advanced,
        };
        var validation = CustomLoopRunValidator.ValidateForDispatch(successor);
        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));
        return successor;
    }

    private static CustomLoopRunRecord ReplaceCompletedEvidence(
        CustomLoopRunRecord run,
        string sourceNodeId,
        Func<CustomLoopRunEvent, CustomLoopRunEvent> substitution)
    {
        var original = run.Events.Single(item => item.StepId == sourceNodeId && item.Kind == CustomLoopRunEventKind.NodeAttemptCompleted);
        var draft = substitution(original) with { SequentialNodeEvidence = null };
        var evidence = CustomLoopSequentialNodeEvidenceHash.Apply(original.SequentialNodeEvidence! with
        {
            OutcomeArtifactHash = CustomLoopSequentialOutcomeArtifactHash.Compute(draft),
            EvidenceHash = string.Empty,
        });
        var replacement = draft with { SequentialNodeEvidence = evidence };
        var frontier = Assert.IsType<GovernedLoopFrontierPosture>(run.Frontier);
        var nodes = frontier.Payload.Nodes.Select(node => string.Equals(node.NodeId, sourceNodeId, StringComparison.Ordinal)
            ? GovernedLoopNodeExecutionEvidence.CreateActivation(
                node.ActivationOrdinal,
                node.PlanOrdinal,
                node.VisitOrdinal,
                node.NodeId,
                node.Descriptor,
                node.IncomingControlEdgeIds,
                node.OutgoingControlEdgeIds,
                node.Status,
                node.Attempt,
                node.AttemptOperationId,
                node.OutcomeEvidenceId,
                evidence.OutcomeArtifactHash,
                node.CycleId,
                node.CycleIteration,
                node.ControlOutcome,
                node.SelectedControlEdgeIds,
                node.SkippedControlEdgeIds,
                node.JoinArrivals)
            : node).ToArray();
        var reboundFrontier = GovernedLoopFrontierPosture.Create(
            frontier.Binding,
            frontier.WorkspaceId,
            frontier.GraphArtifactHash,
            frontier.GraphLayoutHash,
            frontier.AdmissionReceiptHash,
            frontier.Payload.FrontierVersion,
            frontier.Payload.ConcurrencyCeiling,
            frontier.Payload.Status,
            nodes,
            frontier.Payload.UpdatedAtUtc,
            string.Empty);
        return run with
        {
            Events = run.Events.Select(item => string.Equals(item.EventId, original.EventId, StringComparison.Ordinal) ? replacement : item).ToArray(),
            Frontier = reboundFrontier,
        };
    }

    private static CustomLoopRunEvent WithSequentialEvidence(
        CustomLoopRunEvent runEvent,
        GovernedLoopSequentialAdapterBinding binding,
        GovernedLoopSequentialPlanNode node,
        GovernedLoopNodeExecutionEvidence activation,
        CustomLoopSequentialNodeEvidenceKind kind,
        CustomLoopSequentialNodeDisposition disposition)
    {
        var isTerminal = kind == CustomLoopSequentialNodeEvidenceKind.CompletedOutcome;
        var evidence = CustomLoopSequentialNodeEvidenceHash.Apply(new CustomLoopSequentialNodeEvidence(
            CustomLoopSequentialNodeEvidence.CurrentSchemaVersion,
            kind,
            binding.WorkspaceId,
            binding.ExecutionBinding.RunId,
            binding.ExecutionBinding.Revision,
            binding.ExecutionBinding.ExecutionGeneration,
            activation.ActivationOrdinal,
            activation.VisitOrdinal,
            node.NodeId,
            1,
            activation.CycleId,
            activation.CycleIteration,
            isTerminal ? GovernedLoopControlCondition.Success : null,
            isTerminal ? node.OutgoingControlEdgeIds.ToArray() : [],
            [],
            null,
            null,
            disposition,
            CustomLoopSequentialOutcomeArtifactHash.Compute(runEvent),
            string.Empty));
        return runEvent with { SequentialNodeEvidence = evidence };
    }

    private static GovernedLoopFrontierPosture Frontier(GovernedLoopSequentialFrontierTransitionResult result)
    {
        Assert.Equal(GovernedLoopSequentialFrontierTransitionStatus.Applied, result.Status);
        return Assert.IsType<GovernedLoopFrontierPosture>(result.Frontier);
    }

    private sealed record ResolverContext(
        GovernedLoopGraphRevisionArtifact Artifact,
        GovernedLoopSequentialPlan Plan,
        GovernedLoopSequentialInvocationSnapshot Invocation,
        GovernedLoopSequentialAdapterBinding AdapterBinding,
        CustomLoopRunRecord Run);

    private sealed record PreparedEquality(ResolverContext Context, CustomLoopRunRecord Run);
}
