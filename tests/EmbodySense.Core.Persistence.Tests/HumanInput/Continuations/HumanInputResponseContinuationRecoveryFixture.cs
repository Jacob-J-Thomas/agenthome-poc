using EmbodySense.Core.Application.Loops.Admission;
using EmbodySense.Core.Application.Loops.Admission.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Application.Tests.Governance.Authority.Grants;
using EmbodySense.Core.Application.Tests.Loops.Sequential;
using EmbodySense.HumanInputContinuationHost;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses;
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
using EmbodySense.Core.Common.Loops.PureNodes;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Sequential.Models;

namespace EmbodySense.Core.Persistence.Tests.HumanInput.Continuations;

internal static class HumanInputResponseContinuationRecoveryFixture
{
    internal static readonly DateTimeOffset Now = GovernedLoopSequentialApplicationTestFixture.Now;

    internal static HumanInputContinuationRecoveryContext CreateWaitingContext(string runId = "human-input-continuation-run")
        => CreateContext(runId, activeParallel: false);

    internal static HumanInputContinuationRecoveryContext CreateActivePendingContext(string runId = "human-input-active-continuation-run")
        => CreateContext(runId, activeParallel: true);

    private static HumanInputContinuationRecoveryContext CreateContext(string runId, bool activeParallel)
    {
        const string WorkspaceId = "workspace-sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var configuration = new GovernedLoopHumanInputNodeConfiguration(
            GovernedLoopHumanInputNodeConfiguration.CurrentSchemaVersion,
            "confirmation",
            "Collect one bounded private confirmation.",
            "Confirm the governed continuation.",
            new HumanInputResponseSchema(HumanInputResponseKind.Confirmation, null, null, null, null),
            HumanInputPrivacyClass.Private,
            [new HumanInputEligibleRespondent("user-one", "respondent-one", "route-one")],
            new HumanInputResponsePolicy(HumanInputResponsePolicyKind.FirstValid, null, null),
            "timeout-policy-one@revision-one",
            "failure-policy-one@revision-one");
        var artifact = activeParallel
            ? CreateActivePendingArtifact(configuration)
            : HumanInputResponseContinuationGraphFixture.CreateArtifact(configuration);
        var builtPlan = GovernedLoopSequentialPlanBuilder.Build(artifact);
        Assert.True(builtPlan.Plan is not null, $"{builtPlan.Status}: {builtPlan.FailurePath}");
        var plan = builtPlan.Plan!;
        var execution = GovernedLoopExecutionBinding.Create(1, runId, artifact.RevisionArtifact.Revision, 1);
        var context = CustomLoopContextSnapshot.CreateEmpty(Now);
        var invocation = GovernedLoopSequentialContractHash.Apply(new GovernedLoopSequentialInvocationSnapshot(
            1,
            "Continue the exact waiting Human Input request.",
            new CustomLoopModelSnapshot("provider", "model"),
            null,
            Now,
            context.SourceManifest,
            string.Empty));
        var publication = GovernedLoopRevisionPublicationPinFactory.Create(1, artifact.RevisionArtifact.Revision, "publish-sequential", Hash('7'));
        var grantProfile = AuthorityGrantApplicationTestFixture.Profile(ceiling: AuthorityCeilingIntersection.EmptyCeiling());
        var grant = AuthorityGrantApplicationTestFixture.Grant(
            binding: new AuthorityGrantBinding(
                new AuthorityGrantProfilePin(
                    new AuthorityProfileReference(grantProfile.ProfileId, grantProfile.Revision),
                    AuthorityGrantApplicationTestFixture.ProfileHash(grantProfile)),
                artifact.Graph.OwningRole,
                publication),
            ceiling: AuthorityCeilingIntersection.EmptyCeiling(),
            boundary: new AuthorityGrantBoundary(Now.AddHours(-1), Now.AddHours(1), AuthorityGrantCompletionConstraintKind.None),
            recordedAtUtc: Now.AddHours(-2));
        var grantReference = new AuthorityGrantReference(grant.GrantId, grant.Revision, grant.ContentHash);
        var admission = GovernedLoopAdmissionRequestHash.Apply(new GovernedLoopAdmissionRequest(
            1,
            "human-input-continuation-admit",
            invocation.ContentHash,
            string.Empty,
            publication,
            grantReference,
            Actor(),
            "test"));
        var receipt = GovernedLoopSequentialApplicationTestFixture.AdmissionReceipt(artifact, execution, WorkspaceId, admission.OperationId, admission.RequestHash, artifact.ArtifactHash, artifact.LayoutHash, grant);
        var binding = GovernedLoopSequentialContractHash.Apply(new GovernedLoopSequentialAdapterBinding(
            1,
            WorkspaceId,
            execution,
            admission.OperationId,
            receipt,
            receipt.ContentHash,
            admission.RequestHash,
            invocation.ContentHash,
            artifact.ArtifactHash,
            artifact.LayoutHash,
            [],
            string.Empty));
        var projected = GovernedLoopSequentialLegacyDefinitionProjector.Project(binding, invocation, plan, artifact);
        Assert.Equal(GovernedLoopSequentialLegacyDefinitionProjectionStatus.Ready, projected.Status);
        var definition = Assert.IsType<CustomLoopDefinition>(projected.Definition);
        var admittedEvent = AdmittedEvent(binding, activeParallel);
        var initialized = Assert.IsType<GovernedLoopFrontierPosture>(GovernedLoopSequentialFrontierMachine.Initialize(
            binding,
            plan,
            admittedEvent.EventId,
            admittedEvent.EventId,
            admittedEvent.SequentialNodeEvidence!.OutcomeArtifactHash,
            admittedEvent.TimestampUtc).Frontier);
        var seed = CustomLoopAdmissionRequestHash.Apply(new CustomLoopRunRecord(
            CustomLoopRunRecord.CurrentSchemaVersion,
            execution.RunId,
            definition.Id,
            1,
            CustomLoopRunStatus.Admitted,
            Now,
            Now,
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
            [
                admittedEvent,
                new CustomLoopRunEvent(2, "human-input-continuation-admission-audit", Now, CustomLoopRunEventKind.AdmissionAuditCompleted, null, null, null, "Admission audit completed.", [], null, null, null, null, null, null, null, null, null, null),
            ],
            null,
            null,
            null)
        {
            CapabilityAdmission = receipt.Evidence.CapabilityAdmission,
            SequentialInvocationSnapshot = invocation,
            SequentialAdapterBinding = binding,
            Frontier = initialized,
        });
        var selected = GovernedLoopSequentialFrontierMachine.Select(seed.Frontier, binding, plan);
        var node = Assert.IsType<GovernedLoopSequentialPlanNode>(selected.Node);
        var ready = Assert.IsType<GovernedLoopNodeExecutionEvidence>(selected.Activation);
        var started = Assert.IsType<GovernedLoopFrontierPosture>(GovernedLoopSequentialFrontierMachine.Start(
            seed.Frontier,
            binding,
            plan,
            node,
            ready,
            1,
            "human-input-continuation-claim",
            Now.AddMinutes(1)).Frontier);
        var running = seed with
        {
            LifecycleVersion = 2,
            Status = CustomLoopRunStatus.Running,
            UpdatedAtUtc = Now.AddMinutes(1),
            ExecutionClock = new CustomLoopExecutionClock(0, Now.AddMinutes(1)),
            Frontier = started,
        };
        var activation = started.Payload.Nodes[ready.ActivationOrdinal];
        var waitingFrontier = Assert.IsType<GovernedLoopFrontierPosture>(GovernedLoopSequentialFrontierMachine.ParkRunningHumanInput(
            started,
            binding,
            plan,
            node,
            activation,
            1,
            "human-input-continuation-claim",
            Now.AddMinutes(1)).Frontier);
        var checkpoint = CreateCheckpoint(binding, running, node, waitingFrontier.Payload.Nodes[activation.ActivationOrdinal], waitingFrontier, configuration, Now.AddMinutes(1));
        var aggregateWaiting = waitingFrontier.Payload.Status == GovernedLoopFrontierStatus.Waiting;
        var waiting = running with
        {
            LifecycleVersion = 3,
            Status = aggregateWaiting ? CustomLoopRunStatus.Waiting : CustomLoopRunStatus.Running,
            UpdatedAtUtc = Now.AddMinutes(1),
            ExecutionClock = aggregateWaiting
                ? new CustomLoopExecutionClock(60_000, null)
                : new CustomLoopExecutionClock(0, Now.AddMinutes(1)),
            Frontier = waitingFrontier,
            HumanInputWaitingCheckpoints = [checkpoint],
            Events = [.. running.Events, new CustomLoopRunEvent(3, "human-input-continuation-waiting", Now.AddMinutes(1), CustomLoopRunEventKind.LifecycleChanged, null, null, null, "Human Input waiting.", [], null, null, null, null, null, null, null, null, null, null)],
        };
        Assert.True(CustomLoopRunValidator.Validate(waiting).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(waiting).Errors));
        return new HumanInputContinuationRecoveryContext(seed, running, waiting, checkpoint, binding, plan, artifact, grant);
    }

    private static GovernedLoopGraphRevisionArtifact CreateActivePendingArtifact(GovernedLoopHumanInputNodeConfiguration configuration)
    {
        var humanInput = new GovernedLoopNodeDefinition(
            "human-input",
            new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.HumanInput, GovernedLoopHumanInputVocabulary.TypeId, GovernedLoopHumanInputVocabulary.DescriptorVersion),
            [new GovernedLoopPortDefinition(GovernedLoopHumanInputVocabulary.ResponsePortId, GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "confirmation", true)],
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string>(),
            null,
            null,
            null,
            configuration);
        var readyBranch = new GovernedLoopNodeDefinition(
            "ready-branch",
            GovernedLoopSequentialNodeDescriptors.IdentityTransform,
            [
                new GovernedLoopPortDefinition(GovernedLoopPureNodeVocabulary.InputPort, GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data, "text", true),
                new GovernedLoopPortDefinition(GovernedLoopPureNodeVocabulary.OutputPort, GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "text", true),
            ],
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string>());
        var join = new GovernedLoopNodeDefinition(
            "join",
            GovernedLoopSequentialNodeDescriptors.AllJoin,
            [],
            GovernedLoopAuthorityCeiling.Create([]),
            new Dictionary<string, string>());
        return GovernedLoopSequentialApplicationTestFixture.Artifact(
            [
                GovernedLoopSequentialApplicationTestFixture.Trigger("trigger"),
                humanInput,
                readyBranch,
                join,
                GovernedLoopSequentialApplicationTestFixture.Exit("exit"),
            ],
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-human-input", "trigger", humanInput.Id, GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("trigger-to-ready", "trigger", readyBranch.Id, GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("human-input-to-join", humanInput.Id, join.Id, GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("ready-to-join", readyBranch.Id, join.Id, GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("join-to-exit", join.Id, "exit", GovernedLoopControlCondition.Success),
            ],
            ["exit"],
            bindings:
            [
                new GovernedLoopBindingDefinition("request-to-ready", GovernedLoopBindingKind.Data, "trigger", "request", readyBranch.Id, GovernedLoopPureNodeVocabulary.InputPort),
                new GovernedLoopBindingDefinition("request-to-exit", GovernedLoopBindingKind.Data, "trigger", "request", "exit", "result"),
            ],
            valueSchemas:
            [
                new GovernedLoopValueSchemaDefinition("confirmation", GovernedLoopValueKind.Boolean, false),
                new GovernedLoopValueSchemaDefinition("text", GovernedLoopValueKind.Text, false),
            ],
            authorityCeiling: GovernedLoopAuthorityCeiling.Create([GovernedLoopSequentialApplicationTestFixture.ConversationTurnCapabilityId]));
    }

    internal static CustomLoopRunRecord AnsweredNotResumed(HumanInputContinuationRecoveryContext context)
    {
        var request = context.Checkpoint.Request;
        var selection = HumanInputResponseSelectionHash.Apply(new HumanInputResponseSelection(
            HumanInputResponseSelection.CurrentSchemaVersion,
            "human-input-continuation-selection",
            new HumanInputRequestReference(HumanInputRequestReference.CurrentSchemaVersion, request.RequestId, request.RequestVersionId, request.RequestHash),
            request.ResponsePolicy.Kind,
            [],
            null,
            null,
            request.Timing.RequestedAtUtc.AddMinutes(1),
            string.Empty));
        var selectionReference = HumanInputResponseSelectionReference.Create(selection);
        var evidence = GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(new GovernedLoopHumanInputWaitingCheckpointEvidence(
            GovernedLoopHumanInputWaitingCheckpoint.CurrentSchemaVersion,
            context.Checkpoint.Evidence.Length + 1,
            GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Answered,
            selection.SelectedAtUtc,
            selectionReference,
            null,
            null,
            null,
            null,
            context.Checkpoint.Evidence[^1].EvidenceHash,
            string.Empty));
        var answered = GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(new GovernedLoopHumanInputWaitingCheckpoint(
            context.Checkpoint.SchemaVersion,
            context.Checkpoint.Binding,
            context.Checkpoint.NodeConfiguration,
            context.Checkpoint.ResolvedPolicy,
            request,
            GovernedLoopHumanInputWaitingCheckpointPosture.AnsweredNotResumed,
            [.. context.Checkpoint.Evidence, evidence],
            string.Empty));
        var run = context.Run with
        {
            LifecycleVersion = context.Run.LifecycleVersion + 1,
            UpdatedAtUtc = selection.SelectedAtUtc,
            HumanInputWaitingCheckpoints = [answered],
        };
        Assert.True(CustomLoopRunValidator.Validate(run).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(run).Errors));
        return run;
    }

    private static GovernedLoopHumanInputWaitingCheckpoint CreateCheckpoint(
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

    private static CustomLoopRunEvent AdmittedEvent(GovernedLoopSequentialAdapterBinding binding, bool activeParallel)
    {
        var runEvent = new CustomLoopRunEvent(1, "human-input-continuation-admitted", Now, CustomLoopRunEventKind.Admitted, null, null, null, "Run admitted.", [], null, null, null, null, null, null, null, null, null, null);
        var evidence = CustomLoopSequentialNodeEvidenceHash.Apply(new CustomLoopSequentialNodeEvidence(
            1,
            CustomLoopSequentialNodeEvidenceKind.CompletedOutcome,
            binding.WorkspaceId,
            binding.ExecutionBinding.RunId,
            binding.ExecutionBinding.Revision,
            binding.ExecutionBinding.ExecutionGeneration,
            0,
            1,
            "trigger",
            1,
            null,
            null,
            GovernedLoopControlCondition.Always,
            activeParallel ? ["trigger-to-human-input", "trigger-to-ready"] : ["trigger-to-human-input"],
            [],
            null,
            null,
            CustomLoopSequentialNodeDisposition.Completed,
            CustomLoopSequentialOutcomeArtifactHash.Compute(runEvent),
            string.Empty));
        return runEvent with { SequentialNodeEvidence = evidence };
    }

    private static AuthorityActorId Actor()
    {
        Assert.True(AuthorityActorId.TryParse("user-owner", out var actor, out _));
        return actor!;
    }

    private static string Hash(char value) => new(value, 64);
}
