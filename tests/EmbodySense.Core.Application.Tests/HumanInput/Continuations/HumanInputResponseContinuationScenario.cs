using EmbodySense.Core.Application.HumanInput.Continuations;
using EmbodySense.Core.Application.HumanInput.Continuations.Models;
using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Application.HumanInput.Responses;
using EmbodySense.Core.Application.HumanInput.Responses.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Admission;
using EmbodySense.Core.Application.Loops.Admission.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Application.Loops.Wait.Models;
using EmbodySense.Core.Application.Tests.Capabilities;
using EmbodySense.Core.Application.Tests.Governance.Authority.Grants;
using EmbodySense.Core.Application.Tests.HumanInput.Lifecycle;
using EmbodySense.Core.Application.Tests.HumanInput.Responses;
using EmbodySense.Core.Application.Tests.Loops.Sequential;
using EmbodySense.Core.Application.Tests.Loops.Sleep;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses.Models;
using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.HumanInput;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints.Models;
using EmbodySense.Core.Common.Loops.HumanInput.Policies;
using EmbodySense.Core.Common.Loops.HumanInput.Policies.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Sequential.Models;

namespace EmbodySense.Core.Application.Tests.HumanInput.Continuations;

internal sealed class HumanInputResponseContinuationScenario
{
    private static readonly DateTimeOffset _now = GovernedLoopSleepApplicationTestFixture.Now.AddMinutes(-5);

    private HumanInputResponseContinuationScenario(
        HumanInputResponseContinuationService service,
        HumanInputResponseContinuationInMemoryRunStore runs,
        IHumanInputResponseLifecycleStore responses,
        InMemoryGovernedLoopSleepStore sleepStore,
        StubGovernedLoopSleepCurrentPosturePort currentPosture,
        HumanInputResponseContinuationBoundContextPort contexts,
        HumanInputResponseContinuationRecordingOrderedRuntime ordered,
        GovernedLoopSleepService sleep,
        HumanInputResponseContinuationCandidate candidate)
    {
        Service = service;
        Runs = runs;
        Responses = responses;
        SleepStore = sleepStore;
        CurrentPosture = currentPosture;
        Contexts = contexts;
        Ordered = ordered;
        Sleep = sleep;
        Candidate = candidate;
    }

    internal HumanInputResponseContinuationService Service { get; }

    internal HumanInputResponseContinuationInMemoryRunStore Runs { get; }

    internal IHumanInputResponseLifecycleStore Responses { get; }

    internal InMemoryGovernedLoopSleepStore SleepStore { get; }

    internal StubGovernedLoopSleepCurrentPosturePort CurrentPosture { get; }

    internal HumanInputResponseContinuationBoundContextPort Contexts { get; }

    internal HumanInputResponseContinuationRecordingOrderedRuntime Ordered { get; }

    internal GovernedLoopSleepService Sleep { get; }

    internal HumanInputResponseContinuationCandidate Candidate { get; }

    internal static async Task<HumanInputResponseContinuationScenario> CreateAsync(
        bool includeSelection = true,
        bool responseAvailable = true,
        bool corruptResponse = false,
        bool advanceOrderedReentry = false,
        HumanInputRequestLifecycleOperationKind? noResponseTerminalOperation = null,
        bool includeFailureRoute = false,
        HumanInputResponsePolicy? responsePolicy = null,
        IReadOnlyList<HumanInputResponseValue>? selectionValues = null,
        bool requireSelection = true)
    {
        Assert.False(includeSelection && noResponseTerminalOperation is not null);
        var policy = responsePolicy ?? new HumanInputResponsePolicy(HumanInputResponsePolicyKind.FirstValid, null, null);
        var context = CreateWaitingContext(includeFailureRoute, policy);
        var runs = new HumanInputResponseContinuationInMemoryRunStore(context.Run);
        var responses = await ResponseStoreAsync(context.Checkpoint.Request, includeSelection, responseAvailable, corruptResponse, noResponseTerminalOperation, selectionValues, requireSelection);
        var sleepStore = new InMemoryGovernedLoopSleepStore();
        var currentPosture = new StubGovernedLoopSleepCurrentPosturePort
        {
            Result = new GovernedLoopSleepCurrentPostureReadResult(GovernedLoopSleepCurrentPostureReadStatus.Found, ExactPosture(context))
        };
        var ordered = new HumanInputResponseContinuationRecordingOrderedRuntime(runs, advanceOrderedReentry);
        var contexts = new HumanInputResponseContinuationBoundContextPort(new GovernedLoopWaitOrderedContext(context.Anchor, context.Plan, context.Artifact));
        var time = new HumanInputResponseContinuationFixedTimeProvider(GovernedLoopSleepApplicationTestFixture.Now);
        var service = new HumanInputResponseContinuationService(runs, responses, sleepStore, currentPosture, contexts, ordered, time);
        var sleep = new GovernedLoopSleepService(sleepStore, currentPosture, service, service, time);
        service.BindSleep(sleep);
        return new HumanInputResponseContinuationScenario(service, runs, responses, sleepStore, currentPosture, contexts, ordered, sleep, new HumanInputResponseContinuationCandidate(context.Run.Id, context.Checkpoint.Binding.CheckpointId, context.Checkpoint.CheckpointHash));
    }

    private static GovernedLoopSleepCurrentPosture ExactPosture(HumanInputResponseContinuationWaitingContext context)
    {
        var lifecycle = GovernedLoopRunLifecycle.Create(context.Binding.ExecutionBinding, GovernedLoopRunLifecyclePayload.Create(1, context.Run.LifecycleVersion, GovernedLoopRunStatus.Waiting, context.Run.CreatedAtUtc, context.Run.UpdatedAtUtc, null));
        var execution = GovernedLoopExecutionEvidenceSet.Create(1, lifecycle, context.Run.Frontier!, [], []);
        return new GovernedLoopSleepCurrentPosture(execution, context.Binding.AdmissionReceipt.Intent.Publication, true, GovernedLoopSleepApplicationTestFixture.Hash('f'), null, GovernedLoopSleepApplicationTestFixture.Now, GovernedLoopSleepApplicationTestFixture.Hash('9'));
    }

    private static async Task<IHumanInputResponseLifecycleStore> ResponseStoreAsync(
        HumanInputRequest request,
        bool includeSelection,
        bool responseAvailable,
        bool corruptResponse,
        HumanInputRequestLifecycleOperationKind? noResponseTerminalOperation,
        IReadOnlyList<HumanInputResponseValue>? selectionValues,
        bool requireSelection)
    {
        if (corruptResponse)
        {
            return new HumanInputResponseContinuationFixedResponseStore(null, HumanInputResponseLifecycleStoreReadStatus.Ready);
        }
        if (!responseAvailable)
        {
            return new HumanInputResponseContinuationFixedResponseStore(null, HumanInputResponseLifecycleStoreReadStatus.NotFound);
        }

        var grantBinding = AuthorityGrantApplicationTestFixture.Binding() with
        {
            Loop = GovernedLoopRevisionPublicationPinFactory.Create(1, GovernedLoopRevisionReference.Create(1, request.Binding.LoopGraphId, request.Binding.LoopRevisionId, GovernedLoopSleepApplicationTestFixture.Hash('e')), "continuation-response-publish", GovernedLoopSleepApplicationTestFixture.Hash('f')),
        };
        var grant = AuthorityGrantApplicationTestFixture.Grant(
            binding: grantBinding,
            boundary: new AuthorityGrantBoundary(request.Timing.RequestedAtUtc.AddHours(-1), request.Timing.ExpiresAtUtc.AddHours(1), AuthorityGrantCompletionConstraintKind.None),
            recordedAtUtc: request.Timing.RequestedAtUtc.AddMinutes(-5));
        var lifecycle = new HumanInputRequestLifecycleHarness(grant);
        lifecycle.Time.Value = request.Timing.RequestedAtUtc;
        lifecycle.Resolver.Handler = (_, _) => HumanInputRequestLifecycleTestData.ActiveResolution(grant, request.Timing.RequestedAtUtc);
        await HumanInputRequestLifecycleTransitionTestSupport.SeedAsync(lifecycle, request, "continuation-response-request-create");
        if (noResponseTerminalOperation is { } terminalOperation)
        {
            var recordedAtUtc = terminalOperation == HumanInputRequestLifecycleOperationKind.Expire ? request.Timing.ExpiresAtUtc.AddTicks(1) : request.Timing.RequestedAtUtc.AddMinutes(1);
            lifecycle.Time.Value = recordedAtUtc;
            lifecycle.Resolver.Handler = (_, _) => HumanInputRequestLifecycleTestData.ActiveResolution(grant, recordedAtUtc);
            var replacement = terminalOperation == HumanInputRequestLifecycleOperationKind.Supersede
                ? HumanInputRequestHash.Apply(request with { RequestId = "human-input-continuation-replacement", RequestVersionId = "human-input-continuation-replacement-v1", RequestHash = string.Empty })
                : null;
            var terminal = await lifecycle.Service.MutateAsync(HumanInputRequestLifecycleTransitionTestSupport.Command(
                lifecycle,
                terminalOperation,
                "continuation-response-request-" + terminalOperation.ToString().ToLowerInvariant(),
                request.RequestId,
                replacement));
            Assert.Equal(HumanInputRequestLifecycleMutationStatus.Committed, terminal.Status);
        }
        var store = new InMemoryHumanInputResponseLifecycleStore(lifecycle.Store.Snapshot(request.RequestId));
        if (!includeSelection && noResponseTerminalOperation is null)
        {
            var snapshot = store.CurrentSnapshot;
            Assert.NotNull(snapshot);
            Assert.Equal(HumanInputRequestLifecycleStatus.Pending, snapshot.Request.Head.Status);
            Assert.Equal(request.Timing.RequestedAtUtc, snapshot.Request.Head.UpdatedAtUtc);
        }
        if (includeSelection)
        {
            var authenticator = new RecordingHumanInputResponseActorAuthenticator();
            var responses = new HumanInputResponseLifecycleService(store, authenticator, new StubCapabilityAuthorityTransaction(), request.Binding.WorkspaceId, new MutableHumanInputResponseTimeProvider(request.Timing.RequestedAtUtc.AddMinutes(1)));
            var values = selectionValues ?? DefaultSelectionValues(request.ResponsePolicy!);
            foreach (var (value, index) in values.Select((value, index) => (value, index)))
            {
                if (index == 1)
                {
                    authenticator.ActorId = HumanInputResponseLifecycleTestData.Actor("user-two");
                }

                var submitted = await responses.MutateAsync(HumanInputResponseLifecycleTestData.Submit(
                    request,
                    store.CurrentSnapshot!.Request.Head,
                    "continuation-response-submit-" + index,
                    "continuation-response-" + index,
                    value));
                Assert.Equal(HumanInputResponseLifecycleMutationStatus.Committed, submitted.Status);
            }
            if (request.ResponsePolicy!.Kind == HumanInputResponsePolicyKind.ManualSelection)
            {
                Assert.True(HumanInputResponseReference.TryCreate(request, Assert.Single(store.CurrentSnapshot!.Responses), out var selected, out var validation));
                Assert.True(validation.IsValid);
                authenticator.ActorId = HumanInputResponseLifecycleTestData.Actor("selector-one");
                var selectedResult = await responses.MutateAsync(HumanInputResponseLifecycleTestData.Target(
                    request,
                    store.CurrentSnapshot.Request.Head,
                    HumanInputResponseOperationKind.Select,
                    "continuation-response-manual-select",
                    selected!));
                Assert.Equal(HumanInputResponseLifecycleMutationStatus.Committed, selectedResult.Status);
            }
            if (requireSelection)
            {
                Assert.NotNull(store.CurrentSnapshot!.Selection);
            }
        }
        return store;
    }

    private static IReadOnlyList<HumanInputResponseValue> DefaultSelectionValues(HumanInputResponsePolicy policy)
        => policy.Kind is HumanInputResponsePolicyKind.FirstValid or HumanInputResponsePolicyKind.ManualSelection
            ? [HumanInputResponseLifecycleTestData.Text("accepted")]
            : [HumanInputResponseLifecycleTestData.Text("accepted"), HumanInputResponseLifecycleTestData.Text("accepted")];

    private static HumanInputResponseContinuationWaitingContext CreateWaitingContext(bool includeFailureRoute, HumanInputResponsePolicy responsePolicy)
    {
        const string WorkspaceId = "workspace-sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var configuration = new GovernedLoopHumanInputNodeConfiguration(
            GovernedLoopHumanInputNodeConfiguration.CurrentSchemaVersion,
            "text",
            "Collect one bounded private response.",
            "Provide one response.",
            new HumanInputResponseSchema(HumanInputResponseKind.Text, 64, null, null, null),
            HumanInputPrivacyClass.Private,
            EligibleRespondents(responsePolicy),
            responsePolicy,
            "timeout-policy-one@revision-one",
            "failure-policy-one@revision-one");
        var edges = includeFailureRoute
            ? new[]
            {
                new GovernedLoopControlEdgeDefinition("trigger-to-human-input", "trigger", "human-input", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("human-input-to-exit", "human-input", "exit", GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("human-input-failure-to-fail", "human-input", "fail", GovernedLoopControlCondition.Failure),
            }
            : new[]
            {
                new GovernedLoopControlEdgeDefinition("trigger-to-human-input", "trigger", "human-input", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("human-input-to-exit", "human-input", "exit", GovernedLoopControlCondition.Success),
            };
        var nodes = includeFailureRoute
            ? new[]
            {
                GovernedLoopSequentialApplicationTestFixture.Trigger("trigger"),
                new GovernedLoopNodeDefinition(
                    "human-input",
                    new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.HumanInput, GovernedLoopHumanInputVocabulary.TypeId, GovernedLoopHumanInputVocabulary.DescriptorVersion),
                    [new GovernedLoopPortDefinition(GovernedLoopHumanInputVocabulary.ResponsePortId, GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "text", true)],
                    GovernedLoopAuthorityCeiling.Create([]),
                    new Dictionary<string, string>(),
                    null,
                    null,
                    null,
                    configuration),
                GovernedLoopSequentialApplicationTestFixture.Exit("exit"),
                GovernedLoopSequentialApplicationTestFixture.Node("fail", GovernedLoopSequentialNodeDescriptors.FailTerminal),
            }
            :
            [
                GovernedLoopSequentialApplicationTestFixture.Trigger("trigger"),
                new GovernedLoopNodeDefinition(
                    "human-input",
                    new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.HumanInput, GovernedLoopHumanInputVocabulary.TypeId, GovernedLoopHumanInputVocabulary.DescriptorVersion),
                    [new GovernedLoopPortDefinition(GovernedLoopHumanInputVocabulary.ResponsePortId, GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "text", true)],
                    GovernedLoopAuthorityCeiling.Create([]),
                    new Dictionary<string, string>(),
                    null,
                    null,
                    null,
                    configuration),
                GovernedLoopSequentialApplicationTestFixture.Exit("exit"),
            ];
        var artifact = GovernedLoopSequentialApplicationTestFixture.Artifact(
            nodes,
            edges,
            includeFailureRoute ? ["exit", "fail"] : ["exit"],
            bindings: [new GovernedLoopBindingDefinition("response-to-exit", GovernedLoopBindingKind.Data, "human-input", GovernedLoopHumanInputVocabulary.ResponsePortId, "exit", "result")],
            authorityCeiling: GovernedLoopAuthorityCeiling.Create([GovernedLoopSequentialApplicationTestFixture.ConversationTurnCapabilityId]));
        var plan = Assert.IsType<GovernedLoopSequentialPlan>(GovernedLoopSequentialPlanBuilder.Build(artifact).Plan);
        var definition = CustomLoopDefinitionContentHash.Apply(CustomLoopDefinition.CreateSeed("continuation-loop", "role", "step", "continuation-create", _now) with { InferenceSteps = [], CapabilityRequirements = LoopCapabilityRequirements.CreateCustomLoopManifest("continuation-loop", []), ContentHash = string.Empty });
        var execution = GovernedLoopExecutionBinding.Create(1, "human-input-continuation-run", artifact.RevisionArtifact.Revision, 1);
        var admittedAtUtc = GovernedLoopSequentialApplicationTestFixture.Now;
        var context = CustomLoopContextSnapshot.CreateEmpty(admittedAtUtc);
        var invocation = GovernedLoopSequentialContractHash.Apply(new GovernedLoopSequentialInvocationSnapshot(1, "Continue the exact waiting Human Input request.", new CustomLoopModelSnapshot("provider", "model"), null, admittedAtUtc, context.SourceManifest, string.Empty));
        var admission = GovernedLoopAdmissionRequestHash.Apply(new GovernedLoopAdmissionRequest(1, "human-input-continuation-admit", invocation.ContentHash, string.Empty, GovernedLoopRevisionPublicationPinFactory.Create(1, artifact.RevisionArtifact.Revision, "publish-sequential", GovernedLoopSleepApplicationTestFixture.Hash('7')), GrantReference(), Actor(), "test"));
        var receipt = GovernedLoopSequentialApplicationTestFixture.AdmissionReceipt(artifact, execution, WorkspaceId, admission.OperationId, admission.RequestHash, artifact.ArtifactHash, artifact.LayoutHash);
        var binding = GovernedLoopSequentialContractHash.Apply(new GovernedLoopSequentialAdapterBinding(1, WorkspaceId, execution, admission.OperationId, receipt, receipt.ContentHash, admission.RequestHash, invocation.ContentHash, artifact.ArtifactHash, artifact.LayoutHash, [], string.Empty));
        var anchorResult = GovernedLoopSequentialRunAnchorGuard.Create(binding, admission, receipt, invocation, artifact);
        Assert.True(anchorResult.Anchor is not null, anchorResult.Status.ToString());
        var admitted = AdmittedEvent(binding);
        var initial = Assert.IsType<GovernedLoopFrontierPosture>(GovernedLoopSequentialFrontierMachine.Initialize(binding, plan, admitted.EventId, admitted.EventId, admitted.SequentialNodeEvidence!.OutcomeArtifactHash, admitted.TimestampUtc).Frontier);
        var seed = CustomLoopAdmissionRequestHash.Apply(new CustomLoopRunRecord(
            CustomLoopRunRecord.CurrentSchemaVersion,
            execution.RunId,
            definition.Id,
            1,
            CustomLoopRunStatus.Admitted,
            _now,
            _now,
            null,
            "web",
            invocation.ModelSnapshot,
            admission.OperationId,
            "user-owner",
            string.Empty,
            definition,
            invocation.TriggerPrompt,
            null,
            context,
            CustomLoopExecutionClock.NotStarted(),
            CustomLoopRunCheckpoint.Start(),
            [admitted, new CustomLoopRunEvent(2, "human-input-continuation-admission-audit", _now, CustomLoopRunEventKind.AdmissionAuditCompleted, null, null, null, "Admission audit completed.", [], null, null, null, null, null, null, null, null, null, null)],
            null,
            null,
            null)
        {
            CapabilityAdmission = receipt.Evidence.CapabilityAdmission,
            SequentialInvocationSnapshot = invocation,
            SequentialAdapterBinding = binding,
            Frontier = initial,
        });
        var selected = GovernedLoopSequentialFrontierMachine.Select(seed.Frontier, binding, plan);
        var node = Assert.IsType<GovernedLoopSequentialPlanNode>(selected.Node);
        var ready = Assert.IsType<GovernedLoopNodeExecutionEvidence>(selected.Activation);
        var started = Assert.IsType<GovernedLoopFrontierPosture>(GovernedLoopSequentialFrontierMachine.Start(seed.Frontier, binding, plan, node, ready, 1, "human-input-continuation-claim", _now.AddMinutes(1)).Frontier);
        var running = seed with { LifecycleVersion = 2, Status = CustomLoopRunStatus.Running, UpdatedAtUtc = _now.AddMinutes(1), ExecutionClock = new CustomLoopExecutionClock(0, _now.AddMinutes(1)), Frontier = started };
        var activation = started.Payload.Nodes[ready.ActivationOrdinal];
        var waitingFrontier = Assert.IsType<GovernedLoopFrontierPosture>(GovernedLoopSequentialFrontierMachine.ParkRunningHumanInput(started, binding, plan, node, activation, 1, "human-input-continuation-claim", _now.AddMinutes(1)).Frontier);
        var checkpoint = Checkpoint(binding, running, node, waitingFrontier.Payload.Nodes[activation.ActivationOrdinal], waitingFrontier, configuration, _now.AddMinutes(1));
        var waiting = running with
        {
            LifecycleVersion = 3,
            Status = CustomLoopRunStatus.Waiting,
            UpdatedAtUtc = _now.AddMinutes(1),
            ExecutionClock = new CustomLoopExecutionClock(60_000, null),
            Frontier = waitingFrontier,
            HumanInputWaitingCheckpoints = [checkpoint],
            Events = [.. running.Events, new CustomLoopRunEvent(3, "human-input-continuation-waiting", _now.AddMinutes(1), CustomLoopRunEventKind.LifecycleChanged, null, null, null, "Human Input waiting.", [], null, null, null, null, null, null, null, null, null, null)],
        };
        Assert.True(CustomLoopRunValidator.Validate(waiting).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(waiting).Errors));
        Assert.True(GovernedLoopSequentialFrontierMachine.Validate(waiting.Frontier, binding, plan));
        return new HumanInputResponseContinuationWaitingContext(waiting, checkpoint, binding, anchorResult.Anchor!, plan, artifact);
    }

    private static IReadOnlyList<HumanInputEligibleRespondent> EligibleRespondents(HumanInputResponsePolicy policy)
        => policy.Kind == HumanInputResponsePolicyKind.FirstValid
            ? [new HumanInputEligibleRespondent("user-one", "respondent-one", "route-one")]
            : [
                new HumanInputEligibleRespondent("selector-one", "selector-role", "route-selector"),
                new HumanInputEligibleRespondent("user-one", "role-one", "route-one"),
                new HumanInputEligibleRespondent("user-two", "role-two", "route-two"),
            ];

    private static GovernedLoopHumanInputWaitingCheckpoint Checkpoint(
        GovernedLoopSequentialAdapterBinding binding,
        CustomLoopRunRecord run,
        GovernedLoopSequentialPlanNode node,
        GovernedLoopNodeExecutionEvidence activation,
        GovernedLoopFrontierPosture frontier,
        GovernedLoopHumanInputNodeConfiguration configuration,
        DateTimeOffset resolvedAtUtc)
    {
        var timeout = HumanInputPolicyArtifactHash.Apply(new HumanInputPolicyArtifact(1, "timeout-policy-one", "revision-one", HumanInputPolicyKind.ResponseWindow, binding.WorkspaceId, binding.ExecutionBinding.Revision.GraphId, run.AdmissionActor, 60_000, HumanInputTerminalDisposition.Unknown, string.Empty));
        var failure = HumanInputPolicyArtifactHash.Apply(new HumanInputPolicyArtifact(1, "failure-policy-one", "revision-one", HumanInputPolicyKind.DeadlineDisposition, binding.WorkspaceId, binding.ExecutionBinding.Revision.GraphId, run.AdmissionActor, null, HumanInputTerminalDisposition.Expired, string.Empty));
        var resolution = Assert.IsType<HumanInputPolicyResolutionSnapshot>(HumanInputPolicyResolutionSnapshot.TryCreate(binding.WorkspaceId, binding.ExecutionBinding.Revision.GraphId, binding.ExecutionBinding.Revision.RevisionId, node.NodeId, run.AdmissionActor, timeout, failure, resolvedAtUtc));
        const string CheckpointId = "human-input-continuation-checkpoint";
        var request = HumanInputRequestHash.Apply(new HumanInputRequest(
            HumanInputRequest.CurrentSchemaVersion,
            "human-input-continuation-request",
            "human-input-continuation-request-v1",
            new HumanInputRequestBinding(binding.WorkspaceId, binding.ExecutionBinding.Revision.GraphId, binding.ExecutionBinding.Revision.RevisionId, node.NodeId, run.Id, CheckpointId),
            configuration.Purpose!,
            configuration.Prompt!,
            configuration.ResponseSchema!,
            configuration.PrivacyClass,
            configuration.EligibleRespondents!.Select(item => item!).ToArray(),
            new HumanInputTiming(resolution.ResolvedAtUtc, resolution.ExpiresAtUtc),
            configuration.ResponsePolicy!,
            new HumanInputContinuationBinding(HumanInputContinuationPolicyKind.BoundNodeAndCheckpointOnly, node.NodeId, CheckpointId),
            string.Empty));
        var published = GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(new GovernedLoopHumanInputWaitingCheckpointEvidence(1, 1, GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Published, resolvedAtUtc, null, null, null, null, null, string.Empty, string.Empty));
        return GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(new GovernedLoopHumanInputWaitingCheckpoint(
            1,
            new GovernedLoopHumanInputWaitingCheckpointBinding(1, binding.WorkspaceId, binding.ExecutionBinding, binding.AdmissionReceipt.Intent.Publication, binding.GraphArtifactHash, binding.GraphLayoutHash, binding.AdmissionReceiptHash, frontier.Payload.FrontierVersion, frontier.Payload.ContentHash, activation.ActivationOrdinal, activation.CycleId, activation.CycleIteration, node.NodeId, activation.VisitOrdinal, CheckpointId),
            configuration,
            resolution,
            request,
            GovernedLoopHumanInputWaitingCheckpointPosture.Pending,
            [published],
            string.Empty));
    }

    private static CustomLoopRunEvent AdmittedEvent(GovernedLoopSequentialAdapterBinding binding)
    {
        var runEvent = new CustomLoopRunEvent(1, "human-input-continuation-admitted", _now, CustomLoopRunEventKind.Admitted, null, null, null, "Run admitted.", [], null, null, null, null, null, null, null, null, null, null);
        var evidence = CustomLoopSequentialNodeEvidenceHash.Apply(new CustomLoopSequentialNodeEvidence(1, CustomLoopSequentialNodeEvidenceKind.CompletedOutcome, binding.WorkspaceId, binding.ExecutionBinding.RunId, binding.ExecutionBinding.Revision, binding.ExecutionBinding.ExecutionGeneration, 0, 1, "trigger", 1, null, null, GovernedLoopControlCondition.Always, ["trigger-to-human-input"], [], null, null, CustomLoopSequentialNodeDisposition.Completed, CustomLoopSequentialOutcomeArtifactHash.Compute(runEvent), string.Empty));
        return runEvent with { SequentialNodeEvidence = evidence };
    }

    private static AuthorityActorId Actor()
    {
        Assert.True(AuthorityActorId.TryParse("user-owner", out var actor, out _));
        return actor!;
    }

    private static AuthorityGrantReference GrantReference()
    {
        Assert.True(AuthorityGrantId.TryParse("grant-sequential", out var grantId, out _));
        Assert.True(AuthorityGrantRevision.TryParse("1", out var grantRevision, out _));
        return new AuthorityGrantReference(grantId!, grantRevision!, "sha256:" + GovernedLoopSleepApplicationTestFixture.Hash('a'));
    }
}
