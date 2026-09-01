using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Admission;
using EmbodySense.Core.Application.Loops.Admission.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Sequential.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Tests.Support;

namespace EmbodySense.E2ETests.Web;

internal static class HumanReviewBrowserFixture
{
    private const string ConversationTurnCapabilityId = "org.embodysense/conversation-turn";
    public static async Task SeedPendingAsync(WorkspacePaths paths, string runId, string prompt, string reviewerRoleId = "governed-reviewer", TimeSpan? requestLifetime = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var now = DateTimeOffset.UtcNow.AddMinutes(-5);
        var artifact = CreateArtifact(runId, now);
        var planResult = GovernedLoopSequentialPlanBuilder.Build(artifact);
        var plan = planResult.Plan ?? throw new InvalidOperationException($"The browser Human Review fixture graph was not plannable: {planResult.Status}.");
        var context = CustomLoopContextSnapshot.CreateEmpty(now);
        var invocation = GovernedLoopSequentialContractHash.Apply(new GovernedLoopSequentialInvocationSnapshot(
            GovernedLoopSequentialInvocationSnapshot.CurrentSchemaVersion,
            prompt,
            new CustomLoopModelSnapshot("provider", "model"),
            null,
            context.CapturedAtUtc,
            context.SourceManifest,
            string.Empty));
        var publication = GovernedLoopRevisionPublicationPinFactory.Create(1, artifact.RevisionArtifact.Revision, "browser-human-review-publish", Hash('7'));
        var execution = GovernedLoopExecutionBinding.Create(1, runId, publication.Revision, 1);
        if (!AuthorityGrantId.TryParse("grant-browser-human-review", out var grantId, out _)
            || !AuthorityGrantRevision.TryParse("1", out var grantRevision, out _)
            || !AuthorityActorId.TryParse("user-owner", out var actorId, out _))
        {
            throw new InvalidOperationException("The browser Human Review fixture authority identities are invalid.");
        }

        var workspaceId = CapabilityWorkspaceScopeId.Create(paths.RootPath);
        var grantReference = new AuthorityGrantReference(grantId!, grantRevision!, "sha256:" + Hash('a'));
        var admissionOperationId = "browser-human-review-admit-" + runId;
        var admissionRequest = GovernedLoopAdmissionRequestHash.Apply(new GovernedLoopAdmissionRequest(
            GovernedLoopAdmissionRequest.CurrentSchemaVersion,
            admissionOperationId,
            invocation.ContentHash,
            string.Empty,
            publication,
            grantReference,
            actorId!,
            "web"));
        var intent = new GovernedLoopAdmissionIntent(
            GovernedLoopAdmissionIntent.CurrentSchemaVersion,
            workspaceId,
            admissionRequest.OperationId,
            admissionRequest.RequestHash,
            publication,
            grantReference,
            artifact.Graph.OwningRole,
            actorId!,
            admissionRequest.Surface,
            artifact.ArtifactHash,
            artifact.LayoutHash);
        var admissionReceipt = CreateAdmissionReceipt(artifact, execution, intent, workspaceId, now);
        var adapterBinding = GovernedLoopSequentialContractHash.Apply(new GovernedLoopSequentialAdapterBinding(
            GovernedLoopSequentialAdapterBinding.CurrentSchemaVersion,
            workspaceId,
            execution,
            admissionRequest.OperationId,
            admissionReceipt,
            admissionReceipt.ContentHash,
            admissionRequest.RequestHash,
            invocation.ContentHash,
            artifact.ArtifactHash,
            artifact.LayoutHash,
            [],
            string.Empty));
        var materialization = new GovernedLoopSequentialMaterializationRequest(
            GovernedLoopSequentialMaterializationRequest.CurrentSchemaVersion,
            admissionRequest,
            admissionReceipt,
            artifact,
            plan,
            invocation,
            adapterBinding);

        using var store = new CustomLoopRunStore(paths);
        var materialized = await new GovernedLoopSequentialRunMaterializer(
            store,
            new BrowserAuditRecorder(),
            new GovernedLoopSequentialEventIdentityGenerator(),
            new BrowserTimeProvider(now)).MaterializeAsync(materialization).ConfigureAwait(false);
        var admitted = materialized.Run ?? throw new InvalidOperationException($"The browser Human Review fixture run was not materialized: {materialized.Status} {materialized.Detail}");
        var running = TransitionToRunning(admitted);
        var runningMutation = await store.UpdateAsync(running, admitted.LifecycleVersion).ConfigureAwait(false);
        if (runningMutation.Status != CustomLoopRunStoreStatus.Updated)
        {
            throw new InvalidOperationException($"The browser Human Review fixture did not enter Running: {runningMutation.Status}.");
        }

        var started = ClaimReview(running, plan, adapterBinding);
        var startedMutation = await store.UpdateAsync(started, running.LifecycleVersion).ConfigureAwait(false);
        if (startedMutation.Status != CustomLoopRunStoreStatus.Updated)
        {
            throw new InvalidOperationException($"The browser Human Review fixture did not start the review node: {startedMutation.Status}.");
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
        var blocked = blockedTransition.Frontier ?? throw new InvalidOperationException("The browser Human Review fixture did not produce a blocked frontier.");
        var request = CreateRequest(started, blocked, reviewerRoleId, requestLifetime);
        var admission = await new HumanReviewAdmissionService(store).AdmitAsync(new HumanReviewAdmissionCommand(started.Id, started.LifecycleVersion, request, blocked, parkedEvent)).ConfigureAwait(false);
        if (admission.Status != CustomLoopRunStoreStatus.Updated)
        {
            throw new InvalidOperationException($"The browser Human Review fixture request was not admitted: {admission.Status}.");
        }
    }

    private static GovernedLoopGraphRevisionArtifact CreateArtifact(string runId, DateTimeOffset now)
    {
        var nodes = new GovernedLoopNodeDefinition[]
        {
            Trigger("trigger"),
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
            Exit("exit"),
            new GovernedLoopNodeDefinition("fail", GovernedLoopSequentialNodeDescriptors.FailTerminal, [], GovernedLoopAuthorityCeiling.Create([]), new Dictionary<string, string>()),
        };
        var edges = new GovernedLoopControlEdgeDefinition[]
        {
            new("trigger-to-human-review", "trigger", "human-review", GovernedLoopControlCondition.Always),
            new("human-review-to-exit", "human-review", "exit", GovernedLoopControlCondition.Success),
            new("human-review-to-fail", "human-review", "fail", GovernedLoopControlCondition.Failure),
        };
        var owningRole = new ContextualRoleRevisionPin(new ContextualRoleRevisionIdentity("browser-human-review-role", 1), Hash('b'));
        var graph = GovernedLoopGraphDefinition.Create(
            1,
            "browser-human-review-" + runId,
            "revision-1",
            "Park one exact durable browser Human Review request.",
            owningRole,
            "trigger",
            ["exit", "fail"],
            GovernedLoopAuthorityCeiling.Create([ConversationTurnCapabilityId]),
            [new GovernedLoopValueSchemaDefinition("text", GovernedLoopValueKind.Text, false)],
            nodes,
            edges,
            [new GovernedLoopBindingDefinition("request-to-exit", GovernedLoopBindingKind.Data, "trigger", "request", "exit", "result")],
            new GovernedLoopOutputContract("Return the exact bounded result.", [new GovernedLoopOutputDefinition("result", "text", "exit", "published-result", true)]),
            new GovernedLoopDisplayMetadata(
                "Browser Human Review graph",
                "The fixture uses only the canonical review gate.",
                nodes.Select((node, index) => new GovernedLoopNodeDisplayMetadata(node.Id, node.Id, "Node.", index * 100, 0)).ToArray()),
            DefaultRoutingPolicy());
        var revision = GovernedLoopRevisionArtifactFactory.Create(1, graph.RevisionReference, null, null, "browser-human-review-create-" + runId, "user-owner", now);
        return GovernedLoopGraphRevisionArtifactFactory.Create(1, revision, graph);
    }

    private static GovernedLoopAdmissionReceipt CreateAdmissionReceipt(
        GovernedLoopGraphRevisionArtifact artifact,
        GovernedLoopExecutionBinding execution,
        GovernedLoopAdmissionIntent intent,
        string workspaceId,
        DateTimeOffset evaluatedAtUtc)
    {
        if (!CapabilityId.TryParse("org.embodysense/loop-" + artifact.ArtifactHash[..32], out var subject, out _)
            || !CapabilityVersionRange.TryParse("*", out var versions, out _)
            || !CapabilityIntegrityDigest.TryParse("sha256:" + artifact.ArtifactHash, out var checksum, out _))
        {
            throw new InvalidOperationException("The browser Human Review fixture capability identity is invalid.");
        }

        var dependencies = artifact.Graph.AuthorityCeiling.CapabilityIds.Select(value =>
        {
            if (!CapabilityId.TryParse(value, out var id, out _))
            {
                throw new InvalidOperationException("The browser Human Review fixture capability dependency is invalid.");
            }

            return new CapabilityDependency(id!, versions!);
        }).ToArray();
        var manifest = new CapabilityDependencyManifest(
            CapabilityDependencyManifest.CurrentSchemaVersion,
            CapabilityDependencyManifestKind.LoopPackage,
            subject!,
            dependencies,
            [],
            new CapabilityDependencyArtifactMetadata(checksum, null));
        var capabilities = CreateCapabilityAdmission(manifest, workspaceId, evaluatedAtUtc);
        if (!AuthorityProfileId.TryParse("profile-browser-human-review", out var profileId, out _)
            || !AuthorityProfileRevision.TryParse("1", out var profileRevision, out _)
            || !AuthorityProfileHash.TryParse("sha256:" + Hash('c'), out var profileHash, out _))
        {
            throw new InvalidOperationException("The browser Human Review fixture profile identity is invalid.");
        }

        var grantProfile = new AuthorityGrantProfilePin(new AuthorityProfileReference(profileId!, profileRevision!), profileHash!);
        var grantBoundary = new AuthorityGrantBoundary(evaluatedAtUtc.AddHours(-1), evaluatedAtUtc.AddHours(1), AuthorityGrantCompletionConstraintKind.None);
        var effectiveAuthority = AuthorityCeilingIntersection.EmptyCeiling();
        var evidence = GovernedLoopAdmissionContractHash.CreateEmptyModelRoutingAdmission(
            intent,
            execution,
            grantProfile,
            grantBoundary,
            Hash('d'),
            effectiveAuthority,
            capabilities,
            evaluatedAtUtc);
        var admissionEvidence = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionEvidence(
            GovernedLoopAdmissionEvidence.CurrentSchemaVersion,
            GovernedLoopAdmissionContractHash.ComputeIntentHash(intent),
            execution,
            grantProfile,
            grantBoundary,
            Hash('d'),
            effectiveAuthority,
            capabilities,
            evidence,
            GovernedLoopAdmissionContractHash.CreateEvidenceReferences(intent, effectiveAuthority, capabilities, evidence),
            evaluatedAtUtc,
            string.Empty));
        return GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionReceipt(
            GovernedLoopAdmissionReceipt.CurrentSchemaVersion,
            intent,
            admissionEvidence,
            evaluatedAtUtc,
            string.Empty));
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
        return transition.Frontier is null
            ? throw new InvalidOperationException("The browser Human Review fixture review activation did not start.")
            : active with { LifecycleVersion = active.LifecycleVersion + 1, UpdatedAtUtc = updatedAtUtc, Frontier = transition.Frontier };
    }

    private static CustomLoopRunEvent CreateParkingEvent(CustomLoopRunRecord run, GovernedLoopSequentialAdapterBinding binding, GovernedLoopSequentialPlanNode node, GovernedLoopNodeExecutionEvidence activation)
    {
        var timestampUtc = run.UpdatedAtUtc.AddMinutes(1);
        var parked = new CustomLoopRunEvent(run.Events[^1].Sequence + 1, "event-review-park-" + run.Id, timestampUtc, CustomLoopRunEventKind.NodeOutcomeObserved, activation.CycleIteration ?? run.Checkpoint.Iteration, node.NodeId, activation.Attempt, "The exact canonical Human Review node durably parked before its request became observable.", [], null, null, null, null, null, null, null, null, null, null, null);
        var evidence = CustomLoopSequentialNodeEvidenceHash.Apply(new CustomLoopSequentialNodeEvidence(1, CustomLoopSequentialNodeEvidenceKind.ReviewRequested, binding.WorkspaceId, binding.ExecutionBinding.RunId, binding.ExecutionBinding.Revision, binding.ExecutionBinding.ExecutionGeneration, activation.ActivationOrdinal, activation.VisitOrdinal, node.NodeId, activation.Attempt!.Value, activation.CycleId, activation.CycleIteration, null, [], [], null, null, CustomLoopSequentialNodeDisposition.ReviewPending, CustomLoopSequentialOutcomeArtifactHash.Compute(parked), string.Empty));
        return parked with { SequentialNodeEvidence = evidence };
    }

    private static HumanReviewRequest CreateRequest(CustomLoopRunRecord predecessor, GovernedLoopFrontierPosture blocked, string reviewerRoleId, TimeSpan? requestLifetime)
    {
        var activation = Assert.Single(blocked.Payload.Nodes, item => item.Status == GovernedLoopNodeExecutionStatus.ReviewBlocked);
        var hash = Hash('a');
        var binding = HumanReviewContractHash.ApplyBinding(new HumanReviewBinding(1, blocked.WorkspaceId, predecessor.Id, blocked.Binding.Revision.GraphId, blocked.Binding.Revision.RevisionId, blocked.Binding.Revision.ExecutableHash, activation.NodeId, activation.ActivationOrdinal, null, activation.Attempt!.Value, "frontier-" + predecessor.Id, blocked.Payload.FrontierVersion, blocked.Payload.ContentHash, hash, hash, hash, hash, hash, hash, hash, null, string.Empty));
        var scope = HumanReviewContractHash.ApplyApprovalScope(new HumanReviewApprovalScope(HumanReviewApprovalScopeKind.Continuation, binding.BindingHash, null, string.Empty));
        var timing = new HumanReviewTiming(predecessor.UpdatedAtUtc, predecessor.UpdatedAtUtc.Add(requestLifetime ?? TimeSpan.FromMinutes(10)), predecessor.UpdatedAtUtc.AddHours(1));
        var requestId = "review-request-" + predecessor.Id;
        var operationId = "review-operation-" + predecessor.Id;
        return HumanReviewContractHash.ApplyRequest(new HumanReviewRequest(1, requestId, operationId, binding, HumanReviewPurpose.Continuation, ImmutableArray.Create(HumanReviewDecisionKind.Approve, HumanReviewDecisionKind.Reject, HumanReviewDecisionKind.Cancel, HumanReviewDecisionKind.RequestInformation), ImmutableArray.Create(new HumanReviewReviewerScope(reviewerRoleId, ImmutableArray.Create("review-scope-one"))), scope, ImmutableArray.Create(HumanReviewContractHash.ApplyPreview(new HumanReviewRedactedPreview(HumanReviewPreviewKind.Action, "Action", "Redacted action.", string.Empty)), HumanReviewContractHash.ApplyPreview(new HumanReviewRedactedPreview(HumanReviewPreviewKind.Result, "Result", "Redacted result.", string.Empty)), HumanReviewContractHash.ApplyPreview(new HumanReviewRedactedPreview(HumanReviewPreviewKind.Evidence, "Evidence", "Redacted evidence.", string.Empty))), timing, HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.Server, "human-review-browser-fixture", operationId, timing.CreatedAtUtc, string.Empty)), string.Empty));
    }

    private static GovernedLoopNodeDefinition Trigger(string id)
        => new(id, GovernedLoopSequentialNodeDescriptors.ManualTrigger, [Port("request", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data), Port("invocation-context", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Context)], GovernedLoopAuthorityCeiling.Create([]), new Dictionary<string, string>());

    private static GovernedLoopNodeDefinition Exit(string id)
        => new(id, GovernedLoopSequentialNodeDescriptors.SuccessExit, [Port("result", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data), Port("published-result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data)], GovernedLoopAuthorityCeiling.Create([ConversationTurnCapabilityId]), new Dictionary<string, string>());

    private static GovernedLoopPortDefinition Port(string id, GovernedLoopPortDirection direction, GovernedLoopBindingKind kind)
        => new(id, direction, kind, "text", true);

    private static GovernedModelRoutingPolicy DefaultRoutingPolicy()
    {
        if (!CapabilityId.TryParse("org.embodysense/model-profile/codex", out var profileId, out _)
            || !CapabilityDataClass.TryParse("public", out var publicData, out _))
        {
            throw new InvalidOperationException("The browser Human Review fixture routing identity is invalid.");
        }

        var privacy = GovernedModelPrivacyRequirement.Create(1, true, CapabilityEgressMode.None, [], [publicData!], ["local"], GovernedModelRetentionPosture.None, GovernedModelTrainingPosture.Prohibited);
        var unbounded = GovernedModelUsageCeiling.Create(GovernedModelUsageLimit.Unbounded, GovernedModelUsageLimit.Unbounded, GovernedModelUsageLimit.Unbounded, GovernedModelUsageLimit.Unbounded, GovernedModelMonetaryLimit.Unbounded);
        return GovernedModelRoutingPolicy.Create(1, GovernedModelRoutingSelector.Exact(profileId!), [], GovernedModelProfileRequirements.Create(1, [GovernedModelModality.Text], [], 1, 1, privacy, GovernedModelBudgetPolicy.Create(1, unbounded, unbounded, unbounded)));
    }

    private static string Hash(char value) => new(value, 64);

    private static CapabilityAdmissionSnapshot CreateCapabilityAdmission(CapabilityDependencyManifest requirements, string workspaceId, DateTimeOffset admittedAtUtc)
    {
        _ = CapabilityDependencyManifestHash.TryCompute(requirements, out var requirementsHash, out _);
        if (!CapabilityProviderId.TryParse("org.embodysense", out var provider, out _)
            || !CapabilityVersion.TryParse("1.0.0", out var version, out _))
        {
            throw new InvalidOperationException("The browser Human Review fixture capability provider is invalid.");
        }

        var pins = requirements.Required.Select(dependency =>
        {
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dependency.CapabilityId.Value))).ToLowerInvariant();
            if (!CapabilityDescriptorHash.TryParse("sha256:" + digest, out var descriptorHash, out _))
            {
                throw new InvalidOperationException("The browser Human Review fixture capability descriptor hash is invalid.");
            }

            var implementationId = dependency.CapabilityId.Value[(dependency.CapabilityId.Value.IndexOf('/') + 1)..];
            var kind = implementationId switch
            {
                _ when implementationId.StartsWith("model-profile/", StringComparison.Ordinal) => CapabilityKind.ModelProfile,
                _ => CapabilityKind.GraphNode,
            };
            return new CapabilityAdmissionPin(
                new CapabilityDescriptorIdentity(dependency.CapabilityId, version!, descriptorHash!),
                kind,
                new CapabilityImplementationIdentity(provider!, implementationId),
                new CapabilityProvenance(CapabilityProvenanceKind.BuiltIn, "https://embodysense.dev/builtins/" + implementationId, "1", null),
                new CapabilityDependencyArtifactMetadata(null, null),
                "Test-safe description for " + implementationId + ".");
        }).ToArray();
        var evidence = requirements.Required.Select(dependency =>
        {
            var pin = pins.Single(item => item.DescriptorIdentity.Id.Equals(dependency.CapabilityId));
            return new CapabilityAdmissionEvidence(requirements.SubjectId, dependency.CapabilityId, dependency.CompatibleVersionRange, false, "Selected", pin.DescriptorIdentity, "Selected exact browser test capability pin.");
        }).ToArray();
        return new CapabilityAdmissionSnapshot(1, workspaceId, requirements, requirementsHash!.Value, pins, evidence, admittedAtUtc);
    }

    private sealed class BrowserTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class BrowserAuditRecorder : IGovernedLoopSequentialAuditRecorder
    {
        public Task<GovernedLoopSequentialAuditRecordResult> RecordOnceAsync(string operationId, string evidenceHash, AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new GovernedLoopSequentialAuditRecordResult(GovernedLoopSequentialAuditRecordStatus.Recorded, "recorded"));
        }
    }
}
