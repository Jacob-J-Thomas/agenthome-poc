using System.Collections.Immutable;
using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Admission.Models;
using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Admission;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Application.Tests.Loops.Sequential;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Sequential.Models;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

internal static class HumanReviewRecoveryCanonicalRunFactory
{
    public static async Task<CustomLoopRunRecord> CreateApprovedRunAsync(string runId, string admissionOperationId)
    {
        var artifact = GovernedLoopSequentialApplicationTestFixture.Artifact(
            [
                GovernedLoopSequentialApplicationTestFixture.Trigger("trigger"),
                new GovernedLoopNodeDefinition(
                    "human-review",
                    new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.HumanReview, GovernedLoopHumanReviewVocabulary.TypeId, GovernedLoopHumanReviewVocabulary.DescriptorVersion),
                    [],
                    GovernedLoopAuthorityCeiling.Create([]),
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [GovernedLoopHumanReviewNodeCatalogContract.ReviewPolicyIdParameter] = GovernedLoopHumanReviewNodeCatalogContract.LocalReviewerPolicyId,
                        [GovernedLoopHumanReviewNodeCatalogContract.ReviewerRoleIdParameter] = GovernedLoopHumanReviewNodeCatalogContract.LocalReviewerRoleId,
                        [GovernedLoopHumanReviewNodeCatalogContract.ApprovalScopeIdParameter] = "review-scope-one",
                    },
                    null,
                    null,
                    null,
                    null),
                GovernedLoopSequentialApplicationTestFixture.Exit("exit"),
                new GovernedLoopNodeDefinition("fail", GovernedLoopSequentialNodeDescriptors.FailTerminal, [], GovernedLoopAuthorityCeiling.Create([]), new Dictionary<string, string>()),
            ],
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-human-review", "trigger", "human-review", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("human-review-to-exit", "human-review", "exit", GovernedLoopControlCondition.Success),
                new GovernedLoopControlEdgeDefinition("human-review-to-fail", "human-review", "fail", GovernedLoopControlCondition.Failure),
            ],
            ["exit", "fail"],
            bindings: [new GovernedLoopBindingDefinition("request-to-exit", GovernedLoopBindingKind.Data, "trigger", "request", "exit", "result")],
            authorityCeiling: GovernedLoopAuthorityCeiling.Create([GovernedLoopSequentialApplicationTestFixture.ConversationTurnCapabilityId]));
        var planResult = GovernedLoopSequentialPlanBuilder.Build(artifact);
        var plan = planResult.Plan ?? throw new InvalidOperationException($"The canonical recovery test artifact was not plannable: {planResult.Status}.");
        var invocation = GovernedLoopSequentialApplicationTestFixture.InvocationSnapshot(artifact, includeConversation: false);
        var publication = GovernedLoopRevisionPublicationPinFactory.Create(1, artifact.RevisionArtifact.Revision, "publish-sequential", GovernedLoopSequentialApplicationTestFixture.Hash('7'));
        var execution = GovernedLoopExecutionBinding.Create(1, runId, publication.Revision, 1);
        if (!AuthorityGrantId.TryParse("grant-sequential", out var grantId, out _)
            || !AuthorityGrantRevision.TryParse("1", out var grantRevision, out _)
            || !AuthorityActorId.TryParse("user-owner", out var actorId, out _))
        {
            throw new InvalidOperationException("The canonical recovery test authority identifiers were not valid.");
        }

        var grantReference = new AuthorityGrantReference(grantId!, grantRevision!, "sha256:" + GovernedLoopSequentialApplicationTestFixture.Hash('a'));
        var admissionRequest = GovernedLoopAdmissionRequestHash.Apply(new GovernedLoopAdmissionRequest(
            GovernedLoopAdmissionRequest.CurrentSchemaVersion,
            admissionOperationId,
            invocation.ContentHash,
            string.Empty,
            publication,
            grantReference,
            actorId!,
            "test"));
        var receipt = GovernedLoopSequentialApplicationTestFixture.AdmissionReceipt(
            artifact,
            execution,
            "workspace-sha256:" + GovernedLoopSequentialApplicationTestFixture.Hash('f'),
            admissionRequest.OperationId,
            admissionRequest.RequestHash,
            artifact.ArtifactHash,
            artifact.LayoutHash,
            now: GovernedLoopSequentialApplicationTestFixture.Now.AddMinutes(2));
        var adapterBinding = GovernedLoopSequentialContractHash.Apply(new GovernedLoopSequentialAdapterBinding(
            GovernedLoopSequentialAdapterBinding.CurrentSchemaVersion,
            receipt.Intent.WorkspaceId,
            execution,
            admissionRequest.OperationId,
            receipt,
            receipt.ContentHash,
            admissionRequest.RequestHash,
            invocation.ContentHash,
            artifact.ArtifactHash,
            artifact.LayoutHash,
            [],
            string.Empty));
        var materialization = new GovernedLoopSequentialMaterializationRequest(
            GovernedLoopSequentialMaterializationRequest.CurrentSchemaVersion,
            admissionRequest,
            receipt,
            artifact,
            plan,
            invocation,
            adapterBinding);
        var store = new HumanReviewRecoveryCanonicalRunStore();
        var materialized = await new GovernedLoopSequentialRunMaterializer(
            store,
            new HumanReviewRecoveryCanonicalAuditRecorder(),
            new GovernedLoopSequentialEventIdentityGenerator(),
            new HumanReviewRecoveryCanonicalTimeProvider(GovernedLoopSequentialApplicationTestFixture.Now.AddMinutes(2))).MaterializeAsync(materialization);
        var admitted = materialized.Run ?? throw new InvalidOperationException($"The canonical recovery test run was not materialized: {materialized.Status} {materialized.Detail}");
        var running = TransitionToRunning(admitted);
        var runningMutation = await store.UpdateAsync(running, admitted.LifecycleVersion);
        if (runningMutation.Status != CustomLoopRunStoreStatus.Updated || store.Current is null)
        {
            throw new InvalidOperationException($"The canonical recovery test run did not enter the running lifecycle: {runningMutation.Status}.");
        }

        var started = ClaimReview(store.Current, plan, adapterBinding);
        var startedMutation = await store.UpdateAsync(started, running.LifecycleVersion);
        if (startedMutation.Status != CustomLoopRunStoreStatus.Updated || store.Current is null)
        {
            throw new InvalidOperationException($"The canonical recovery test review attempt did not start: {startedMutation.Status}.");
        }

        var reviewNode = plan.Nodes.Single(item => item.Descriptor.Kind == GovernedLoopNodeKind.HumanReview);
        var reviewActivation = started.Frontier!.Payload.Nodes.Single(item => string.Equals(item.NodeId, reviewNode.NodeId, StringComparison.Ordinal));
        var parkedEvent = CreateParkingEvent(started, adapterBinding, reviewNode, reviewActivation);
        var blockedTransition = GovernedLoopSequentialFrontierMachine.ReviewBlockRunning(
            started.Frontier,
            adapterBinding,
            plan,
            reviewNode,
            reviewActivation,
            reviewActivation.Attempt!.Value,
            reviewActivation.AttemptOperationId!,
            parkedEvent.EventId,
            CustomLoopSequentialOutcomeArtifactHash.Compute(parkedEvent),
            parkedEvent.TimestampUtc);
        var blocked = blockedTransition.Frontier ?? throw new InvalidOperationException("The canonical recovery test review frontier did not block.");
        var request = CreateRequest(started, blocked);
        var admission = await new HumanReviewAdmissionService(store).AdmitAsync(new HumanReviewAdmissionCommand(started.Id, started.LifecycleVersion, request, blocked, parkedEvent));
        if (admission.Status != CustomLoopRunStoreStatus.Updated || store.Current is null)
        {
            throw new InvalidOperationException($"The canonical recovery test review admission failed: {admission.Status}.");
        }

        var decision = await new HumanReviewDecisionService(
            store,
            new HumanReviewRecoveryServerAuthorizer(),
            new HumanReviewRecoveryTrustedClock(store.Current.UpdatedAtUtc.AddMinutes(1))).DecideAsync(new HumanReviewDecisionCommand(store.Current.Id, store.Current.LifecycleVersion, "decision-" + runId, HumanReviewDecisionKind.Approve, null));
        if (decision.Status != HumanReviewDecisionServiceStatus.Accepted || store.Current.HumanReview?.AcceptedTerminalDecision?.Kind != HumanReviewDecisionKind.Approve || !CustomLoopRunValidator.Validate(store.Current).IsValid)
        {
            throw new InvalidOperationException($"The canonical recovery test review approval failed: {decision.Status}.");
        }

        return store.Current;
    }

    private static CustomLoopRunRecord TransitionToRunning(CustomLoopRunRecord admitted)
    {
        var updatedAtUtc = admitted.UpdatedAtUtc.AddMinutes(1);
        var lifecycle = new CustomLoopRunEvent(admitted.Events[^1].Sequence + 1, "event-running-" + admitted.Id, updatedAtUtc, CustomLoopRunEventKind.LifecycleChanged, null, null, null, "Run entered its canonical running lifecycle.", [], null, null, null, null, null, null, null, null, null, null, null, ControlExpectedLifecycleVersion: admitted.LifecycleVersion);
        return admitted with
        {
            LifecycleVersion = admitted.LifecycleVersion + 1,
            Status = CustomLoopRunStatus.Running,
            UpdatedAtUtc = updatedAtUtc,
            ExecutionClock = new CustomLoopExecutionClock(0, updatedAtUtc),
            Events = [.. admitted.Events, lifecycle],
        };
    }

    private static CustomLoopRunRecord ClaimReview(CustomLoopRunRecord active, GovernedLoopSequentialPlan plan, GovernedLoopSequentialAdapterBinding binding)
    {
        var node = plan.Nodes.Single(item => item.Descriptor.Kind == GovernedLoopNodeKind.HumanReview);
        var selection = GovernedLoopSequentialFrontierMachine.Select(active.Frontier, binding, plan);
        var updatedAtUtc = active.UpdatedAtUtc.AddMinutes(1);
        var transition = GovernedLoopSequentialFrontierMachine.Start(active.Frontier, binding, plan, node, selection.Activation, 1, "review-attempt-" + active.Id, updatedAtUtc);
        if (transition.Frontier is null)
        {
            throw new InvalidOperationException("The canonical recovery test review activation did not start.");
        }

        return active with { LifecycleVersion = active.LifecycleVersion + 1, UpdatedAtUtc = updatedAtUtc, Frontier = transition.Frontier };
    }

    private static CustomLoopRunEvent CreateParkingEvent(
        CustomLoopRunRecord run,
        GovernedLoopSequentialAdapterBinding binding,
        GovernedLoopSequentialPlanNode node,
        GovernedLoopNodeExecutionEvidence activation)
    {
        var timestampUtc = run.UpdatedAtUtc.AddMinutes(1);
        var parked = new CustomLoopRunEvent(
            run.Events[^1].Sequence + 1,
            "event-review-park-" + run.Id,
            timestampUtc,
            CustomLoopRunEventKind.NodeOutcomeObserved,
            activation.CycleIteration ?? run.Checkpoint.Iteration,
            node.NodeId,
            activation.Attempt,
            "The exact canonical Human Review node durably parked before its request became observable.",
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
            null);
        var evidence = CustomLoopSequentialNodeEvidenceHash.Apply(new CustomLoopSequentialNodeEvidence(
            1,
            CustomLoopSequentialNodeEvidenceKind.ReviewRequested,
            binding.WorkspaceId,
            binding.ExecutionBinding.RunId,
            binding.ExecutionBinding.Revision,
            binding.ExecutionBinding.ExecutionGeneration,
            activation.ActivationOrdinal,
            activation.VisitOrdinal,
            node.NodeId,
            activation.Attempt!.Value,
            activation.CycleId,
            activation.CycleIteration,
            null,
            [],
            [],
            null,
            null,
            CustomLoopSequentialNodeDisposition.ReviewPending,
            CustomLoopSequentialOutcomeArtifactHash.Compute(parked),
            string.Empty));
        return parked with { SequentialNodeEvidence = evidence };
    }

    private static HumanReviewRequest CreateRequest(CustomLoopRunRecord predecessor, GovernedLoopFrontierPosture blocked)
    {
        var activation = Assert.Single(blocked.Payload.Nodes, item => item.Status == GovernedLoopNodeExecutionStatus.ReviewBlocked);
        var hash = GovernedLoopSequentialApplicationTestFixture.Hash('a');
        var binding = HumanReviewContractHash.ApplyBinding(new HumanReviewBinding(1, blocked.WorkspaceId, predecessor.Id, blocked.Binding.Revision.GraphId, blocked.Binding.Revision.RevisionId, blocked.Binding.Revision.ExecutableHash, activation.NodeId, activation.ActivationOrdinal, null, activation.Attempt!.Value, "frontier-" + predecessor.Id, blocked.Payload.FrontierVersion, blocked.Payload.ContentHash, hash, hash, hash, hash, hash, hash, hash, null, string.Empty));
        var scope = HumanReviewContractHash.ApplyApprovalScope(new HumanReviewApprovalScope(HumanReviewApprovalScopeKind.Continuation, binding.BindingHash, null, string.Empty));
        var timing = new HumanReviewTiming(predecessor.UpdatedAtUtc, predecessor.UpdatedAtUtc.AddMinutes(10), predecessor.UpdatedAtUtc.AddHours(1));
        var requestId = "review-request-" + predecessor.Id;
        var operationId = "review-operation-" + predecessor.Id;
        return HumanReviewContractHash.ApplyRequest(new HumanReviewRequest(1, requestId, operationId, binding, HumanReviewPurpose.Continuation, ImmutableArray.Create(HumanReviewDecisionKind.Approve, HumanReviewDecisionKind.Reject, HumanReviewDecisionKind.Cancel, HumanReviewDecisionKind.RequestInformation), ImmutableArray.Create(new HumanReviewReviewerScope("governed-reviewer", ImmutableArray.Create("review-scope-one"))), scope, ImmutableArray.Create(HumanReviewContractHash.ApplyPreview(new HumanReviewRedactedPreview(HumanReviewPreviewKind.Action, "Action", "Redacted action.", string.Empty)), HumanReviewContractHash.ApplyPreview(new HumanReviewRedactedPreview(HumanReviewPreviewKind.Result, "Result", "Redacted result.", string.Empty)), HumanReviewContractHash.ApplyPreview(new HumanReviewRedactedPreview(HumanReviewPreviewKind.Evidence, "Evidence", "Redacted evidence.", string.Empty))), timing, HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.Server, "human-review-store", operationId, timing.CreatedAtUtc, string.Empty)), string.Empty));
    }
}
