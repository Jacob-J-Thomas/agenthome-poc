using System.Collections.Immutable;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.ContextualRoles;
using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.Governance.Authority.Models;
using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Admission;
using EmbodySense.Core.Application.Loops.Admission.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Application.Loops.Execution.Effects;
using EmbodySense.Core.Application.Loops.EffectAttempts.Models;
using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.Loops.Revisions;
using EmbodySense.Core.Application.Loops.GraphAuthoring.Models;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.LocalWorkspace.Actions;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Sequential;
using EmbodySense.Core.Common.Loops.Sequential.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Loops.EffectAttempts;
using EmbodySense.Core.Persistence.Authority;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.ContextualRoles;
using EmbodySense.Core.Persistence.Loops.GraphAuthoring;
using EmbodySense.Core.Persistence.Loops.Admission;
using EmbodySense.Core.Persistence.Loops.Revisions;
using EmbodySense.Core.Persistence.Permissions;
using EmbodySense.Core.Startup.Capabilities;
using EmbodySense.Core.Startup.Loops.Execution.Effects;
using EmbodySense.Tests.Support;

namespace EmbodySense.E2ETests.Web;

internal static class HumanReviewBrowserFixture
{
    private const string ConversationTurnCapabilityId = "org.embodysense/conversation-turn";
    private const string PreDispatchEffectInput = "{\"precondition\":{\"kind\":\"expectedAbsent\"},\"schemaVersion\":1,\"scopeId\":\"workspace\",\"segments\":[{\"kind\":\"literalUtf8\",\"literal\":\"reviewed marker\"}],\"target\":\"shared/process-observable-marker.txt\"}";
    private const string WorkspaceCommandCapabilityId = "org.embodysense/workspace-command";
    public static async Task SeedPendingAsync(WorkspacePaths paths, string runId, string prompt, string reviewerRoleId = "governed-reviewer", TimeSpan? requestLifetime = null, bool includePreDispatchEffect = false, bool makeEffectAmbiguous = false, string? capabilityTrustRoot = null, DateTimeOffset? requestExpiresAtUtc = null, string? publicPreviewDetail = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityTrustRoot);

        var now = DateTimeOffset.UtcNow.AddMinutes(-5);
        var workspaceId = CapabilityWorkspaceScopeId.Create(paths.RootPath);
        var role = CreateBrowserHumanReviewRole(workspaceId, now, includePreDispatchEffect);
        var artifact = CreateArtifact(runId, now, new ContextualRoleRevisionPin(role.Identity, role.ContentHash), includePreDispatchEffect);
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
        var publication = GovernedLoopRevisionPublicationPinFactory.Create(1, artifact.RevisionArtifact.Revision, "browser-human-review-publish-" + runId, Hash('7'));
        var execution = GovernedLoopExecutionBinding.Create(1, runId, publication.Revision, 1);
        if (!AuthorityActorId.TryParse("user-owner", out var actorId, out _))
        {
            throw new InvalidOperationException("The browser Human Review fixture authority identities are invalid.");
        }

        var authority = await SeedCanonicalAuthorityDependenciesAsync(paths, capabilityTrustRoot!, artifact, publication, role, workspaceId, runId, now, includePreDispatchEffect).ConfigureAwait(false);
        var grantReference = authority.GrantReference;
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
        var admissionReceipt = CreateAdmissionReceipt(artifact, execution, intent, workspaceId, now, authority.GrantProfile, authority.GrantBoundary, authority.DependencyEvidenceHash, authority.EffectiveAuthority);
        var admissionOutcome = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionTerminalOutcome(
            GovernedLoopAdmissionTerminalOutcome.CurrentSchemaVersion,
            intent,
            GovernedLoopAdmissionDisposition.Admitted,
            admissionReceipt,
            null,
            now,
            string.Empty));
        var admissionValidation = GovernedLoopAdmissionValidator.Validate(admissionOutcome);
        if (!admissionValidation.IsValid)
        {
            throw new InvalidOperationException("The browser Human Review fixture admission outcome is invalid: " + string.Join(',', admissionValidation.Errors));
        }

        var canonicalAdmissionStore = new GovernedLoopAdmissionStore(paths, new FileCapabilityCatalogTrustProvider(capabilityTrustRoot));
        var admissionRead = await canonicalAdmissionStore.ReadByOperationAsync(workspaceId, admissionRequest.OperationId).ConfigureAwait(false);
        if (admissionRead.Status != GovernedLoopAdmissionStoreReadStatus.NotFound || admissionRead.Outcome is not null)
        {
            throw new InvalidOperationException($"The browser Human Review fixture admission identity was not empty: {admissionRead.Status}.");
        }

        var admissionCommit = await canonicalAdmissionStore.CommitAsync(new GovernedLoopAdmissionStoreMutation(
            workspaceId,
            admissionRequest.OperationId,
            admissionRequest.RequestHash,
            GovernedLoopAdmissionContractHash.ComputeIntentHash(intent),
            admissionRead.StoreGeneration,
            admissionOutcome)).ConfigureAwait(false);
        if (admissionCommit.Status != GovernedLoopAdmissionStoreCommitStatus.Committed)
        {
            throw new InvalidOperationException($"The browser Human Review fixture admission outcome was not committed: {admissionCommit.Status}.");
        }

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

        var started = ClaimReviewTarget(running, plan, adapterBinding, includePreDispatchEffect);
        var startedMutation = await store.UpdateAsync(started, running.LifecycleVersion).ConfigureAwait(false);
        if (startedMutation.Status != CustomLoopRunStoreStatus.Updated)
        {
            throw new InvalidOperationException($"The browser Human Review fixture did not start the review node: {startedMutation.Status}.");
        }

        var reviewNode = plan.Nodes.Single(item => includePreDispatchEffect
            ? GovernedLoopSequentialNodeDescriptors.IsRecoverableAction(item.Descriptor)
            : GovernedLoopSequentialNodeDescriptors.IsHumanReview(item.Descriptor));
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
        var request = await CreateRequest(started, blocked, reviewerRoleId, requestLifetime, includePreDispatchEffect, makeEffectAmbiguous, paths, requestExpiresAtUtc, publicPreviewDetail).ConfigureAwait(false);
        var admission = await new HumanReviewAdmissionService(store).AdmitAsync(new HumanReviewAdmissionCommand(started.Id, started.LifecycleVersion, request, blocked, parkedEvent)).ConfigureAwait(false);
        if (admission.Status != CustomLoopRunStoreStatus.Updated)
        {
            throw new InvalidOperationException($"The browser Human Review fixture request was not admitted: {admission.Status}.");
        }
        if (includePreDispatchEffect)
        {
            var durable = admission.Run ?? throw new InvalidOperationException("The browser Human Review fixture did not return its admitted run.");
            var reviewed = durable.HumanReview?.Request.Binding.EffectAttempt ?? throw new InvalidOperationException("The browser Human Review fixture did not retain its reviewed effect binding.");
            var source = new CanonicalHumanReviewEffectEvidenceSource(store, new GovernedLoopEffectAttemptStore(paths));
            var evidence = await source.ReadAsync(new HumanReviewCurrentEffectAttemptEvidenceQuery(durable.HumanReview!.Request.Binding, reviewed)).ConfigureAwait(false);
            if (evidence.Status != HumanReviewCurrentEffectAttemptEvidenceReadStatus.Current || evidence.Evidence is null)
            {
                throw new InvalidOperationException($"The browser Human Review fixture effect evidence is not canonical: {evidence.Status}.");
            }
        }
    }

    internal static async Task<HumanReviewContinuationAuthorityReadStatus> ReadCurrentContinuationAuthorityAsync(WorkspacePaths paths, string capabilityTrustRoot, string runId)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityTrustRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        using var runStore = new CustomLoopRunStore(paths);
        var run = await runStore.GetAsync(runId).ConfigureAwait(false) ?? throw new InvalidOperationException("The browser Human Review fixture run was not found for authority revalidation.");
        var adapter = run.SequentialAdapterBinding ?? throw new InvalidOperationException("The browser Human Review fixture adapter binding was not retained.");
        var review = run.HumanReview ?? throw new InvalidOperationException("The browser Human Review fixture request was not retained.");
        var transaction = new CapabilityAuthorityTransaction(paths);
        var trust = new FileCapabilityCatalogTrustProvider(capabilityTrustRoot);
        var lifecycleStore = new GovernedLoopRevisionLifecycleStore(paths, trust, authorityTransaction: transaction);
        var graphStore = new GovernedLoopGraphRevisionStore(paths, lifecycleStore, trust, authorityTransaction: transaction);
        var artifactRead = await graphStore.ReadArtifactAsync(adapter.ExecutionBinding.Revision).ConfigureAwait(false);
        var artifact = artifactRead.Artifact ?? throw new InvalidOperationException($"The browser Human Review fixture graph was unavailable for authority revalidation: {artifactRead.Status}.");
        using var roleStore = new ContextualRoleRevisionStore(paths, adapter.WorkspaceId, authorityTransaction: transaction);
        var authorityStore = new AuthorityProfileStore(paths, trust, authorityTransaction: transaction);
        var publicationSource = new GovernedLoopPublishedRevisionSource(lifecycleStore, transaction);
        var bindingSource = new GovernedLoopGrantBindingSource(publicationSource, graphStore, transaction);
        var roleSource = new AuthorityGrantRoleSource(adapter.WorkspaceId, roleStore, roleStore, new WorkspaceContextualRoleInstructionSourceProbe(paths), transaction);
        var resolver = new AuthorityGrantResolver(authorityStore, new AuthorityGrantProfileSource(authorityStore), roleSource, publicationSource, bindingSource, transaction);
        var capabilities = CapabilityAdmissionFactory.Create(paths, trust, transaction);
        var source = new CurrentHumanReviewContinuationAuthoritySource(resolver, capabilities);
        var result = await source.ReadAsync(new HumanReviewContinuationAuthorityQuery(review.Request.Binding, adapter, artifact)).ConfigureAwait(false);
        return result.Status;
    }


    private static GovernedLoopGraphRevisionArtifact CreateArtifact(string runId, DateTimeOffset now, ContextualRoleRevisionPin owningRole, bool includePreDispatchEffect)
    {
        var reviewNode = includePreDispatchEffect
            ? new GovernedLoopNodeDefinition(
                "workspace-action",
                GovernedLoopSequentialNodeDescriptors.WorkspaceWrite,
                [Port("result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data)],
                GovernedLoopAuthorityCeiling.Create([WorkspaceCommandCapabilityId]),
                new Dictionary<string, string> { ["input"] = PreDispatchEffectInput })
            : new GovernedLoopNodeDefinition(
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
                null);
        var nodes = new GovernedLoopNodeDefinition[]
        {
            Trigger("trigger"),
            reviewNode,
            Exit("exit"),
            new GovernedLoopNodeDefinition("fail", GovernedLoopSequentialNodeDescriptors.FailTerminal, [], GovernedLoopAuthorityCeiling.Create([]), new Dictionary<string, string>()),
        };
        var reviewNodeId = reviewNode.Id;
        var edges = new GovernedLoopControlEdgeDefinition[]
        {
            new("trigger-to-review", "trigger", reviewNodeId, GovernedLoopControlCondition.Always),
            new("review-to-exit", reviewNodeId, "exit", GovernedLoopControlCondition.Success),
            new("review-to-fail", reviewNodeId, "fail", GovernedLoopControlCondition.Failure),
        };
        var authorityCeiling = includePreDispatchEffect
            ? GovernedLoopAuthorityCeiling.Create([ConversationTurnCapabilityId, WorkspaceCommandCapabilityId])
            : GovernedLoopAuthorityCeiling.Create([ConversationTurnCapabilityId]);
        var bindings = includePreDispatchEffect
            ? new[] { new GovernedLoopBindingDefinition("action-result-to-exit", GovernedLoopBindingKind.Data, reviewNodeId, "result", "exit", "result") }
            : new[] { new GovernedLoopBindingDefinition("request-to-exit", GovernedLoopBindingKind.Data, "trigger", "request", "exit", "result") };
        var graph = GovernedLoopGraphDefinition.Create(
            1,
            "browser-human-review-" + runId,
            "revision-1",
            "Park one exact durable browser Human Review request.",
            owningRole,
            "trigger",
            ["exit", "fail"],
            authorityCeiling,
            [new GovernedLoopValueSchemaDefinition("text", GovernedLoopValueKind.Text, false)],
            nodes,
            edges,
            bindings,
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
        DateTimeOffset evaluatedAtUtc,
        AuthorityGrantProfilePin grantProfile,
        AuthorityGrantBoundary grantBoundary,
        string dependencyEvidenceHash,
        AuthorityCeiling effectiveAuthority)
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
        var evidence = GovernedLoopAdmissionContractHash.CreateEmptyModelRoutingAdmission(
            intent,
            execution,
            grantProfile,
            grantBoundary,
            dependencyEvidenceHash,
            effectiveAuthority,
            capabilities,
            evaluatedAtUtc);
        var admissionEvidence = GovernedLoopAdmissionContractHash.Apply(new GovernedLoopAdmissionEvidence(
            GovernedLoopAdmissionEvidence.CurrentSchemaVersion,
            GovernedLoopAdmissionContractHash.ComputeIntentHash(intent),
            execution,
            grantProfile,
            grantBoundary,
            dependencyEvidenceHash,
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

    private static ContextualRoleRevision CreateBrowserHumanReviewRole(string workspaceId, DateTimeOffset recordedAtUtc, bool includePreDispatchEffect)
    {
        var revision = new ContextualRoleRevision(
            ContextualRoleLimits.SchemaVersion,
            new ContextualRoleRevisionIdentity("browser-human-review-role", 1),
            string.Empty,
            "Browser Human Review role",
            "Test-only role for the exact server-owned browser Human Review authority journey.",
            ContextualRoleStatus.Published,
            new ContextualRoleProvenance("browser-e2e", recordedAtUtc, recordedAtUtc),
            new ContextualRoleWorkspaceApplicability(ImmutableArray.Create(workspaceId)),
            new ContextualRoleInstructionSourceReference(ContextualRoleInstructionSourceKind.WorkspaceRoleMarkdown, "role", ContextualRoleInstructionClassification.RoleInstruction),
            new ContextualRolePolicyMaxima(includePreDispatchEffect
                ? ImmutableArray.Create(ConversationTurnCapabilityId, WorkspaceCommandCapabilityId)
                : ImmutableArray.Create(ConversationTurnCapabilityId)));
        return ContextualRoleRevisionContentHash.Apply(revision);
    }

    private static async Task<(AuthorityGrantReference GrantReference, AuthorityGrantProfilePin GrantProfile, AuthorityGrantBoundary GrantBoundary, string DependencyEvidenceHash, AuthorityCeiling EffectiveAuthority)> SeedCanonicalAuthorityDependenciesAsync(
        WorkspacePaths paths,
        string capabilityTrustRoot,
        GovernedLoopGraphRevisionArtifact artifact,
        GovernedLoopRevisionPublicationPin publication,
        ContextualRoleRevision role,
        string workspaceId,
        string runId,
        DateTimeOffset recordedAtUtc,
        bool includePreDispatchEffect)
    {
        var transaction = new CapabilityAuthorityTransaction(paths);
        using var roleStore = new ContextualRoleRevisionStore(paths, workspaceId, authorityTransaction: transaction);
        var roleRead = await roleStore.ReadAsync(new ContextualRoleRevisionReadRequest(role.Identity)).ConfigureAwait(false);
        if (roleRead.Status == ContextualRoleRevisionReadStatus.NotFound)
        {
            var roleRequest = ContextualRoleRevisionMutationRequestHash.Apply(new ContextualRoleRevisionMutationRequest(
                "create-browser-human-review-role",
                string.Empty,
                ContextualRoleRevisionMutationKind.Create,
                role.Identity.RoleId,
                "browser-e2e",
                role,
                null,
                recordedAtUtc));
            var roleMutation = await roleStore.MutateAsync(roleRequest).ConfigureAwait(false);
            if (roleMutation.Status != ContextualRoleRevisionMutationStatus.Accepted)
            {
                throw new InvalidOperationException($"The browser Human Review fixture role was not persisted: {roleMutation.Status}.");
            }
        }
        else if (roleRead.Status != ContextualRoleRevisionReadStatus.Found || roleRead.Revision is null || !string.Equals(roleRead.Revision.ContentHash, role.ContentHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The browser Human Review fixture role could not be reused exactly: {roleRead.Status}.");
        }

        var trust = new FileCapabilityCatalogTrustProvider(capabilityTrustRoot);
        var lifecycleStore = new GovernedLoopRevisionLifecycleStore(paths, trust, authorityTransaction: transaction);
        var graphStore = new GovernedLoopGraphRevisionStore(paths, lifecycleStore, trust, authorityTransaction: transaction);
        var createOperationId = artifact.RevisionArtifact.CreationOperationId;
        var createRequestHash = Hash('1');
        var createAuthoringHash = Hash('2');
        var draftHead = GovernedLoopRevisionLifecycleHeadFactory.Create(
            1,
            artifact.Graph.GraphId,
            1,
            GovernedLoopRevisionLifecycleStatus.Draft,
            artifact.RevisionArtifact.Revision,
            null,
            createOperationId,
            recordedAtUtc);
        var draftEvidence = GovernedLoopRevisionOperationEvidenceFactory.Create(
            1,
            createOperationId,
            "user-owner",
            createRequestHash,
            GovernedLoopRevisionOperationKind.CreateDraft,
            GovernedLoopRevisionOperationOutcome.Committed,
            GovernedLoopRevisionOperationFailureCode.None,
            null,
            draftHead,
            artifact.RevisionArtifact.Revision,
            null,
            null,
            Hash('3'),
            null,
            recordedAtUtc);
        var draftRead = await graphStore.ReadForMutationAsync(artifact.Graph.GraphId, createOperationId, createRequestHash, createAuthoringHash).ConfigureAwait(false);
        if (draftRead.Status != GovernedLoopRevisionStoreReadStatus.NotFound)
        {
            var lifecycleRead = await lifecycleStore.ReadForMutationAsync(artifact.Graph.GraphId, createOperationId, createRequestHash).ConfigureAwait(false);
            throw new InvalidOperationException($"The browser Human Review fixture graph was not empty: {draftRead.Status} (lifecycle {lifecycleRead.Status}, generation {draftRead.StoreGeneration}).");
        }

        var draftCommit = await graphStore.CommitAsync(new GovernedLoopGraphRevisionStoreMutation(
            new GovernedLoopRevisionStoreMutation(artifact.Graph.GraphId, draftRead.StoreGeneration, draftEvidence, artifact.RevisionArtifact, draftHead),
            artifact.Graph,
            createAuthoringHash,
            Hash('4'))).ConfigureAwait(false);
        if (draftCommit.Status is not (GovernedLoopRevisionStoreCommitStatus.Committed or GovernedLoopRevisionStoreCommitStatus.Replayed))
        {
            throw new InvalidOperationException($"The browser Human Review fixture graph draft was not persisted: {draftCommit.Status}.");
        }

        var publishOperationId = publication.PublicationOperationId;
        var publishRequestHash = Hash('5');
        var publishAuthoringHash = Hash('6');
        var publishedHead = GovernedLoopRevisionLifecycleHeadFactory.Create(
            1,
            artifact.Graph.GraphId,
            draftHead.LifecycleVersion + 1,
            GovernedLoopRevisionLifecycleStatus.Published,
            null,
            publication,
            publishOperationId,
            recordedAtUtc.AddSeconds(1));
        var publishEvidence = GovernedLoopRevisionOperationEvidenceFactory.Create(
            1,
            publishOperationId,
            "browser-e2e",
            publishRequestHash,
            GovernedLoopRevisionOperationKind.Publish,
            GovernedLoopRevisionOperationOutcome.Committed,
            GovernedLoopRevisionOperationFailureCode.None,
            draftHead,
            publishedHead,
            null,
            artifact.RevisionArtifact.Revision,
            null,
            Hash('7'),
            publication.ValidationEvidenceHash,
            recordedAtUtc.AddSeconds(1));
        var publishRead = await graphStore.ReadForMutationAsync(artifact.Graph.GraphId, publishOperationId, publishRequestHash, publishAuthoringHash).ConfigureAwait(false);
        if (publishRead.Status != GovernedLoopRevisionStoreReadStatus.Ready || publishRead.Snapshot is null)
        {
            throw new InvalidOperationException($"The browser Human Review fixture graph draft could not be reread: {publishRead.Status}.");
        }

        var publishCommit = await graphStore.CommitAsync(new GovernedLoopGraphRevisionStoreMutation(
            new GovernedLoopRevisionStoreMutation(artifact.Graph.GraphId, publishRead.StoreGeneration, publishEvidence, null, publishedHead),
            null,
            publishAuthoringHash,
            publication.ValidationEvidenceHash)).ConfigureAwait(false);
        if (publishCommit.Status is not (GovernedLoopRevisionStoreCommitStatus.Committed or GovernedLoopRevisionStoreCommitStatus.Replayed))
        {
            throw new InvalidOperationException($"The browser Human Review fixture graph was not published: {publishCommit.Status}.");
        }

        var authorityStore = new AuthorityProfileStore(paths, trust, authorityTransaction: transaction);
        var profileRead = await authorityStore.ReadAsync("human-review-browser").ConfigureAwait(false);
        if (profileRead.Status is not (AuthorityProfileReadStatus.Available or AuthorityProfileReadStatus.RecoveredLastProved) || profileRead.Record is null || profileRead.Record.CurrentProfile.Status != AuthorityProfileStatus.Active)
        {
            throw new InvalidOperationException($"The browser Human Review fixture authority profile was not active: {profileRead.Status}.");
        }

        var profile = profileRead.Record;
        var binding = new AuthorityGrantBinding(
            new AuthorityGrantProfilePin(new AuthorityProfileReference(profile.ProfileId, profile.CurrentProfile.Revision), profile.CurrentHash),
            new ContextualRoleRevisionPin(role.Identity, role.ContentHash),
            publication);
        var grantIdText = "grant-browser-human-review-" + runId;
        if (!AuthorityGrantId.TryParse(grantIdText, out var grantId, out _)
            || !AuthorityActorId.TryParse("user-owner", out var grantActor, out _)
            || !AuthorityPurpose.TryParse("Browser Human Review browser grant.", out var grantPurpose, out _))
        {
            throw new InvalidOperationException("The browser Human Review fixture grant identity is invalid.");
        }

        if (!AuthorityGrantRevision.TryParse("1", out var grantRevision, out _))
        {
            throw new InvalidOperationException("The browser Human Review fixture grant revision is invalid.");
        }

        var grantBoundary = new AuthorityGrantBoundary(recordedAtUtc.AddMinutes(-1), recordedAtUtc.AddHours(1), AuthorityGrantCompletionConstraintKind.None);
        var grant = AuthorityGrantHash.Apply(new AuthorityGrant(
            AuthorityGrantContractLimits.CurrentSchemaVersion,
            grantId!,
            grantRevision!,
            null,
            null,
            AuthorityGrantLifecycleStatus.Active,
            binding,
            includePreDispatchEffect ? CreatePreDispatchEffectAuthorityCeiling() : AuthorityCeilingIntersection.EmptyCeiling(),
            grantBoundary,
            grantActor!,
            grantPurpose!,
            recordedAtUtc,
            string.Empty));
        var grantOperationId = "create-browser-human-review-grant-" + runId;
        var grantRequestHash = Hash('8');
        var observed = await authorityStore.ReadForMutationAsync(grant.GrantId, grantOperationId, grantRequestHash).ConfigureAwait(false);
        var grantEvidence = new AuthorityGrantOperationEvidence(
            AuthorityGrantContractLimits.CurrentSchemaVersion,
            grantOperationId,
            grantRequestHash,
            AuthorityGrantOperationKind.Create,
            AuthorityGrantOperationOutcome.Committed,
            AuthorityGrantOperationFailureCode.None,
            grant.GrantId,
            0,
            new AuthorityGrantReference(grant.GrantId, grant.Revision, grant.ContentHash),
            grantActor!,
            grantPurpose!,
            Hash('9'),
            Hash('a'),
            recordedAtUtc);
        var grantCommit = await authorityStore.CommitAsync(new AuthorityGrantStoreMutation(observed.StoreGeneration, grant, grantEvidence)).ConfigureAwait(false);
        if (grantCommit.Status is not (AuthorityGrantStoreCommitStatus.Committed or AuthorityGrantStoreCommitStatus.Replayed))
        {
            throw new InvalidOperationException($"The browser Human Review fixture grant was not persisted: {grantCommit.Status}.");
        }

        var publicationSource = new GovernedLoopPublishedRevisionSource(lifecycleStore, transaction);
        var bindingSource = new GovernedLoopGrantBindingSource(publicationSource, graphStore, transaction);
        var roleSource = new AuthorityGrantRoleSource(workspaceId, roleStore, roleStore, new WorkspaceContextualRoleInstructionSourceProbe(paths), transaction);
        var resolver = new AuthorityGrantResolver(authorityStore, new AuthorityGrantProfileSource(authorityStore), roleSource, publicationSource, bindingSource, transaction);
        var reference = new AuthorityGrantReference(grant.GrantId, grant.Revision, grant.ContentHash);
        var resolution = await resolver.ResolveAsync(reference).ConfigureAwait(false);
        if (resolution.Status != AuthorityGrantResolutionStatus.Active || string.IsNullOrWhiteSpace(resolution.DependencyEvidenceHash))
        {
            throw new InvalidOperationException($"The browser Human Review fixture grant dependencies were not active: {resolution.Status}.");
        }

        return (reference, binding.Profile, grantBoundary, resolution.DependencyEvidenceHash, resolution.EffectiveCeiling);
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

    private static CustomLoopRunRecord ClaimReviewTarget(CustomLoopRunRecord active, GovernedLoopSequentialPlan plan, GovernedLoopSequentialAdapterBinding binding, bool includePreDispatchEffect)
    {
        var node = plan.Nodes.Single(item => includePreDispatchEffect
            ? GovernedLoopSequentialNodeDescriptors.IsRecoverableAction(item.Descriptor)
            : GovernedLoopSequentialNodeDescriptors.IsHumanReview(item.Descriptor));
        var selection = GovernedLoopSequentialFrontierMachine.Select(active.Frontier, binding, plan);
        var updatedAtUtc = active.UpdatedAtUtc.AddMinutes(1);
        var transition = GovernedLoopSequentialFrontierMachine.Start(active.Frontier, binding, plan, node, selection.Activation, 1, "review-attempt-" + active.Id, updatedAtUtc);
        return transition.Frontier is null
            ? throw new InvalidOperationException("The browser Human Review fixture review target activation did not start.")
            : active with { LifecycleVersion = active.LifecycleVersion + 1, UpdatedAtUtc = updatedAtUtc, Frontier = transition.Frontier };
    }

    private static CustomLoopRunEvent CreateParkingEvent(CustomLoopRunRecord run, GovernedLoopSequentialAdapterBinding binding, GovernedLoopSequentialPlanNode node, GovernedLoopNodeExecutionEvidence activation)
    {
        var timestampUtc = run.UpdatedAtUtc.AddMinutes(1);
        var parked = new CustomLoopRunEvent(run.Events[^1].Sequence + 1, "event-review-park-" + run.Id, timestampUtc, CustomLoopRunEventKind.NodeOutcomeObserved, activation.CycleIteration ?? run.Checkpoint.Iteration, node.NodeId, activation.Attempt, "The exact canonical Human Review node durably parked before its request became observable.", [], null, null, null, null, null, null, null, null, null, null, null);
        var evidence = CustomLoopSequentialNodeEvidenceHash.Apply(new CustomLoopSequentialNodeEvidence(1, CustomLoopSequentialNodeEvidenceKind.ReviewRequested, binding.WorkspaceId, binding.ExecutionBinding.RunId, binding.ExecutionBinding.Revision, binding.ExecutionBinding.ExecutionGeneration, activation.ActivationOrdinal, activation.VisitOrdinal, node.NodeId, activation.Attempt!.Value, activation.CycleId, activation.CycleIteration, null, [], [], null, null, CustomLoopSequentialNodeDisposition.ReviewPending, CustomLoopSequentialOutcomeArtifactHash.Compute(parked), string.Empty));
        return parked with { SequentialNodeEvidence = evidence };
    }

    private static async Task<HumanReviewRequest> CreateRequest(CustomLoopRunRecord predecessor, GovernedLoopFrontierPosture blocked, string reviewerRoleId, TimeSpan? requestLifetime, bool includePreDispatchEffect, bool makeEffectAmbiguous, WorkspacePaths paths, DateTimeOffset? requestExpiresAtUtc, string? publicPreviewDetail)
    {
        var activation = Assert.Single(blocked.Payload.Nodes, item => item.Status == GovernedLoopNodeExecutionStatus.ReviewBlocked);
        var adapter = predecessor.SequentialAdapterBinding ?? throw new InvalidOperationException("The browser Human Review fixture adapter binding is unavailable.");
        var admitted = adapter.AdmissionReceipt.Evidence;
        var binding = HumanReviewContractHash.ApplyBinding(new HumanReviewBinding(
            1,
            blocked.WorkspaceId,
            predecessor.Id,
            blocked.Binding.Revision.GraphId,
            blocked.Binding.Revision.RevisionId,
            blocked.Binding.Revision.ExecutableHash,
            activation.NodeId,
            activation.ActivationOrdinal,
            null,
            activation.Attempt!.Value,
            "frontier-" + predecessor.Id,
            blocked.Payload.FrontierVersion,
            blocked.Payload.ContentHash,
            admitted.GrantProfile.ContentHash.Value["sha256:".Length..],
            admitted.GrantDependencyEvidenceHash,
            GovernedLoopAdmissionContractHash.ComputeCapabilityAdmissionReferenceHash(admitted.CapabilityAdmission),
            admitted.ModelRoutingAdmission.ContentHash,
            Hash('a'),
            Hash('a'),
            Hash('a'),
            null,
            string.Empty));
        GovernedLoopEffectAttempt? effectAttempt = null;
        if (includePreDispatchEffect)
        {
            effectAttempt = await CreateEffectAttemptAsync(predecessor, activation, paths).ConfigureAwait(false);
            binding = HumanReviewContractHash.ApplyBinding(binding with
            {
                TargetHash = effectAttempt.TargetFingerprint,
                PreconditionHash = effectAttempt.PreconditionEvidenceHash!,
                PayloadHash = effectAttempt.InputFingerprint,
                BindingHash = string.Empty,
            });
            var reviewed = HumanReviewContractHash.ApplyEffectAttempt(new HumanReviewEffectAttemptBinding(effectAttempt.Payload.EffectId, effectAttempt.Payload.OperationId, effectAttempt.Payload.EffectGeneration, effectAttempt.Payload.IntentHash, HumanReviewEffectReleaseContract.CreatePreparation(binding, effectAttempt).PreparationHash, HumanReviewEffectDispatchCertainty.NotDispatched, string.Empty));
            binding = HumanReviewContractHash.ApplyBinding(binding with { EffectAttempt = reviewed, BindingHash = string.Empty });
            var effectStore = new GovernedLoopEffectAttemptStore(paths);
            var begun = await effectStore.BeginAsync(effectAttempt).ConfigureAwait(false);
            if (begun.Status is not (GovernedLoopEffectAttemptStoreStatus.Created or GovernedLoopEffectAttemptStoreStatus.Replayed))
            {
                throw new InvalidOperationException($"The browser Human Review fixture effect attempt was not persisted: {begun.Status}.");
            }

            if (makeEffectAmbiguous)
            {
                using var lease = begun.Lease ?? throw new InvalidOperationException("The browser Human Review fixture effect attempt did not return a lease.");
                var authorized = GovernedLoopEffectAttemptContract.AttachDispatchAuthority(effectAttempt, Hash('9'), effectAttempt.Payload.UpdatedAtUtc.AddSeconds(1));
                var advanced = await effectStore.CompareExchangeAsync(effectAttempt.ContentHash, authorized, lease).ConfigureAwait(false);
                if (advanced.Status != GovernedLoopEffectAttemptStoreStatus.Created)
                {
                    throw new InvalidOperationException($"The browser Human Review fixture effect authority was not persisted: {advanced.Status}.");
                }

                var dispatched = GovernedLoopEffectAttemptContract.Advance(authorized, GovernedLoopEffectPhase.DispatchBoundaryReached, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Pending, null, null, authorized.Payload.UpdatedAtUtc.AddSeconds(1));
                var crossed = await effectStore.CompareExchangeAsync(authorized.ContentHash, dispatched, lease).ConfigureAwait(false);
                if (crossed.Status != GovernedLoopEffectAttemptStoreStatus.Created)
                {
                    throw new InvalidOperationException($"The browser Human Review fixture dispatch boundary was not persisted: {crossed.Status}.");
                }

                var ambiguous = GovernedLoopEffectAttemptContract.Advance(dispatched, GovernedLoopEffectPhase.ReconciliationRequired, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Incomplete, null, null, dispatched.Payload.UpdatedAtUtc.AddSeconds(1));
                var reconciled = await effectStore.CompareExchangeAsync(dispatched.ContentHash, ambiguous, lease).ConfigureAwait(false);
                if (reconciled.Status != GovernedLoopEffectAttemptStoreStatus.Created)
                {
                    throw new InvalidOperationException($"The browser Human Review fixture ambiguous evidence was not persisted: {reconciled.Status}.");
                }
            }
            else
            {
                begun.Lease?.Dispose();
            }
        }

        var scope = HumanReviewContractHash.ApplyApprovalScope(new HumanReviewApprovalScope(includePreDispatchEffect ? HumanReviewApprovalScopeKind.PreDispatchEffect : HumanReviewApprovalScopeKind.Continuation, binding.BindingHash, binding.EffectAttempt?.EffectAttemptId, string.Empty));
        var lifetime = requestLifetime ?? TimeSpan.FromHours(1);
        var createdAtUtc = blocked.Payload.UpdatedAtUtc;
        var expiresAtUtc = requestExpiresAtUtc ?? createdAtUtc.Add(lifetime);
        var dueAtUtc = requestExpiresAtUtc is null
            ? lifetime < TimeSpan.FromMinutes(10) ? createdAtUtc : createdAtUtc.AddMinutes(10)
            : createdAtUtc;
        var timing = new HumanReviewTiming(createdAtUtc, dueAtUtc, expiresAtUtc);
        var requestId = "review-request-" + predecessor.Id;
        var operationId = "review-operation-" + predecessor.Id;
        var previewDetail = publicPreviewDetail ?? "Redacted action.";
        return HumanReviewContractHash.ApplyRequest(new HumanReviewRequest(1, requestId, operationId, binding, includePreDispatchEffect ? HumanReviewPurpose.PreDispatchEffect : HumanReviewPurpose.Continuation, ImmutableArray.Create(HumanReviewDecisionKind.Approve, HumanReviewDecisionKind.Reject, HumanReviewDecisionKind.Cancel, HumanReviewDecisionKind.RequestInformation), ImmutableArray.Create(new HumanReviewReviewerScope(reviewerRoleId, ImmutableArray.Create("review-scope-one"))), scope, ImmutableArray.Create(HumanReviewContractHash.ApplyPreview(new HumanReviewRedactedPreview(HumanReviewPreviewKind.Action, "Action", previewDetail, string.Empty)), HumanReviewContractHash.ApplyPreview(new HumanReviewRedactedPreview(HumanReviewPreviewKind.Result, "Result", "Redacted result.", string.Empty)), HumanReviewContractHash.ApplyPreview(new HumanReviewRedactedPreview(HumanReviewPreviewKind.Evidence, "Evidence", "Redacted evidence.", string.Empty))), timing, HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.Server, "human-review-browser-fixture", operationId, timing.CreatedAtUtc, string.Empty)), string.Empty));
    }

    private static async Task<GovernedLoopEffectAttempt> CreateEffectAttemptAsync(CustomLoopRunRecord predecessor, GovernedLoopNodeExecutionEvidence activation, WorkspacePaths paths)
    {
        if (predecessor.SequentialAdapterBinding is not { } adapter
            || activation.Attempt is null
            || string.IsNullOrWhiteSpace(activation.AttemptOperationId)
            || !GovernedActuatorInputContract.TryCanonicalize(PreDispatchEffectInput, out var input, out _))
        {
            throw new InvalidOperationException("The browser Human Review fixture effect identity is invalid.");
        }

        var permissionPolicy = new PermissionPolicyStore().Load(paths);
        var registry = GovernedWorkspaceActionFactory.CreateRegistry(paths, new CapabilityAuthorityTransaction(paths), new ToolPermissionService(paths, permissionPolicy));
        var descriptor = registry.Descriptors.Single(item => string.Equals(item.OperationId, WorkspaceActionOperationIds.Write, StringComparison.Ordinal));
        if (!registry.TryResolve(descriptor, out var operation)
            || operation is null
            || await operation.PrepareAsync(input!).ConfigureAwait(false) is not { } preparation)
        {
            throw new InvalidOperationException("The browser Human Review fixture workspace effect could not be prepared.");
        }

        var pin = adapter.AdmissionReceipt.Evidence.CapabilityAdmission.Pins.Single(item => string.Equals(item.DescriptorIdentity.Id.Value, WorkspaceCommandCapabilityId, StringComparison.Ordinal));
        if (!Equals(pin.DescriptorIdentity, descriptor.Capability)
            || !Equals(pin.Implementation, descriptor.Implementation))
        {
            throw new InvalidOperationException("The browser Human Review fixture workspace effect pin is not exact.");
        }

        var effectIdentity = WorkspaceActionFingerprint.Compute(
            "embodysense.workspace-tool-effect.v1",
            adapter.ExecutionBinding.RunId,
            activation.NodeId,
            activation.Attempt.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            activation.AttemptOperationId,
            activation.AttemptOperationId,
            WorkspaceActionOperationIds.Write,
            PreDispatchEffectInput);
        return GovernedLoopEffectAttemptContract.Prepare(
            GovernedLoopExecutionBinding.Create(adapter.ExecutionBinding.SchemaVersion, adapter.ExecutionBinding.RunId, adapter.ExecutionBinding.Revision, adapter.ExecutionBinding.ExecutionGeneration),
            activation.NodeId,
            activation.Attempt.Value,
            pin.DescriptorIdentity,
            pin.Implementation,
            descriptor.OperationId,
            descriptor.ContentHash,
            "effect-" + effectIdentity,
            "operation-" + effectIdentity,
            1,
            input!.Fingerprint,
            preparation.TargetFingerprint,
            preparation.PreconditionEvidenceHash,
            adapter.AdmissionReceipt.ContentHash,
            preparation.BeforeEvidenceId,
            predecessor.UpdatedAtUtc.AddSeconds(1));
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

    internal static AuthorityCeiling CreatePreDispatchEffectAuthorityCeiling()
    {
        var capabilities = new[] { ConversationTurnCapabilityId, WorkspaceCommandCapabilityId }
            .Select(value => BuiltInCapabilityCatalog.Descriptors.Single(item => string.Equals(item.Id.Value, value, StringComparison.Ordinal)))
            .ToArray();
        var identities = capabilities.Select(capability =>
        {
            if (!CapabilityDescriptorIdentity.TryCreate(capability, out var identity, out _))
            {
                throw new InvalidOperationException("The browser Human Review fixture built-in capability identity is invalid.");
            }
            return identity!;
        }).ToArray();
        var dataClasses = capabilities.SelectMany(item => item.Requirements.DataClasses).Distinct().ToArray();
        return new AuthorityCeiling(identities, dataClasses, 1, CapabilitySideEffectClass.LocalReversible, false, false, false);
    }

    private static string Hash(char value) => new(value, 64);

    private static CapabilityAdmissionSnapshot CreateCapabilityAdmission(CapabilityDependencyManifest requirements, string workspaceId, DateTimeOffset admittedAtUtc)
    {
        _ = CapabilityDependencyManifestHash.TryCompute(requirements, out var requirementsHash, out _);
        var pins = requirements.Required.Select(dependency =>
        {
            var descriptor = BuiltInCapabilityCatalog.Descriptors.SingleOrDefault(item => item.Id.Equals(dependency.CapabilityId));
            if (descriptor is null
                || !CapabilityDescriptorIdentity.TryCreate(descriptor, out var identity, out _))
            {
                throw new InvalidOperationException("The browser Human Review fixture capability descriptor is unavailable.");
            }

            return new CapabilityAdmissionPin(
                identity!,
                descriptor.Kind,
                descriptor.Implementation,
                descriptor.Provenance,
                new CapabilityDependencyArtifactMetadata(null, null),
                descriptor.Purpose);
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
